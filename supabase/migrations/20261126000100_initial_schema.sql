create extension if not exists pgcrypto with schema extensions;

create table public.app_user (
    uid uuid primary key references auth.users (id) on delete cascade,
    email varchar(255) unique,
    display_name varchar(80),
    created_at timestamptz not null default now()
);

create table public.project (
    pid uuid primary key default gen_random_uuid(),
    owner_uid uuid not null references public.app_user (uid) on delete restrict,
    title varchar(200) not null check (length(btrim(title)) between 1 and 200),
    description varchar(255),
    created_at timestamptz not null default now()
);

create table public.project_member (
    pid uuid not null references public.project (pid) on delete cascade,
    uid uuid not null references public.app_user (uid) on delete cascade,
    nickname varchar(80) not null check (length(btrim(nickname)) between 1 and 80),
    role varchar(80) not null default 'member' check (role in ('owner', 'admin', 'member')),
    joined_at timestamptz not null default now(),
    primary key (pid, uid)
);

create table public.project_invite (
    invite_id uuid primary key default gen_random_uuid(),
    pid uuid not null references public.project (pid) on delete cascade,
    code_hash varchar(255) not null unique,
    expires_at timestamptz,
    revoked_at timestamptz,
    created_at timestamptz not null default now()
);

create table public.link (
    lid uuid primary key default gen_random_uuid(),
    pid uuid not null references public.project (pid) on delete cascade,
    url varchar(2048) not null check (url ~* '^https?://'),
    source_platform varchar(80),
    created_by_uid uuid not null references public.app_user (uid) on delete restrict,
    created_at timestamptz not null default now()
);

create table public.task (
    tid uuid primary key default gen_random_uuid(),
    pid uuid not null references public.project (pid) on delete cascade,
    title varchar(200) not null check (length(btrim(title)) between 1 and 200),
    description varchar(255),
    status varchar(80) not null default 'todo' check (status in ('todo', 'in_progress', 'done')),
    due_at timestamptz,
    created_by_uid uuid not null references public.app_user (uid) on delete restrict,
    assigned_uid uuid references public.app_user (uid) on delete set null,
    created_at timestamptz not null default now(),
    constraint task_assignee_must_be_member
        foreign key (pid, assigned_uid)
        references public.project_member (pid, uid) on delete set null (assigned_uid)
);

create index project_owner_uid_idx on public.project (owner_uid);
create index project_member_uid_idx on public.project_member (uid);
create index project_invite_pid_idx on public.project_invite (pid);
create index link_pid_created_at_idx on public.link (pid, created_at desc);
create index task_pid_status_due_at_idx on public.task (pid, status, due_at);
create index task_assigned_uid_idx on public.task (assigned_uid);

create or replace function public.handle_auth_user_change()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    insert into public.app_user (uid, email, display_name)
    values (
        new.id,
        new.email,
        nullif(btrim(new.raw_user_meta_data ->> 'display_name'), '')
    )
    on conflict (uid) do update
        set email = excluded.email;

    return new;
end;
$$;

create trigger on_auth_user_created_or_email_updated
    after insert or update of email on auth.users
    for each row execute function public.handle_auth_user_change();

create or replace function public.is_project_member(target_pid uuid)
returns boolean
language sql
stable
security definer
set search_path = public, pg_temp
as $$
    select exists (
        select 1
        from public.project_member as member
        where member.pid = target_pid
          and member.uid = auth.uid()
    );
$$;

create or replace function public.has_project_role(target_pid uuid, allowed_roles text[])
returns boolean
language sql
stable
security definer
set search_path = public, pg_temp
as $$
    select exists (
        select 1
        from public.project_member as member
        where member.pid = target_pid
          and member.uid = auth.uid()
          and member.role = any (allowed_roles)
    );
$$;

create or replace function public.create_project(
    project_title text,
    project_description text,
    member_nickname text
)
returns uuid
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    new_pid uuid;
    current_uid uuid := auth.uid();
begin
    if current_uid is null then
        raise exception 'Sign in is required to create a project.';
    end if;

    if project_title is null or length(btrim(project_title)) not between 1 and 200 then
        raise exception 'Project title must be between 1 and 200 characters.';
    end if;

    if project_description is not null and length(project_description) > 255 then
        raise exception 'Project description must be at most 255 characters.';
    end if;

    if member_nickname is null or length(btrim(member_nickname)) not between 1 and 80 then
        raise exception 'Project nickname must be between 1 and 80 characters.';
    end if;

    insert into public.project (owner_uid, title, description)
    values (current_uid, btrim(project_title), nullif(btrim(project_description), ''))
    returning pid into new_pid;

    insert into public.project_member (pid, uid, nickname, role)
    values (new_pid, current_uid, btrim(member_nickname), 'owner');

    return new_pid;
end;
$$;

create or replace function public.create_project_invite(
    target_pid uuid,
    invite_expires_at timestamptz default null
)
returns text
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    invite_code text;
begin
    if auth.uid() is null or not public.has_project_role(target_pid, array['owner', 'admin']) then
        raise exception 'Only project owners and admins can create invite codes.';
    end if;

    if invite_expires_at is not null and invite_expires_at <= now() then
        raise exception 'Invite expiration must be in the future.';
    end if;

    invite_code := upper(encode(extensions.gen_random_bytes(24), 'hex'));

    insert into public.project_invite (pid, code_hash, expires_at)
    values (
        target_pid,
        encode(extensions.digest(convert_to(lower(invite_code), 'UTF8'), 'sha256'), 'hex'),
        invite_expires_at
    );

    return invite_code;
end;
$$;

create or replace function public.join_project_with_invite(
    invite_code text,
    member_nickname text
)
returns uuid
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    current_uid uuid := auth.uid();
    target_pid uuid;
begin
    if current_uid is null then
        raise exception 'Sign in is required to join a project.';
    end if;

    if invite_code is null or length(btrim(invite_code)) < 16 then
        raise exception 'The invite code is invalid or expired.';
    end if;

    if member_nickname is null or length(btrim(member_nickname)) not between 1 and 80 then
        raise exception 'Project nickname must be between 1 and 80 characters.';
    end if;

    select invite.pid into target_pid
    from public.project_invite as invite
    where invite.code_hash = encode(
              extensions.digest(convert_to(lower(btrim(invite_code)), 'UTF8'), 'sha256'),
              'hex'
          )
      and invite.revoked_at is null
      and (invite.expires_at is null or invite.expires_at > now())
    limit 1
    for update;

    if target_pid is null then
        raise exception 'The invite code is invalid or expired.';
    end if;

    insert into public.project_member (pid, uid, nickname, role)
    values (target_pid, current_uid, btrim(member_nickname), 'member')
    on conflict (pid, uid) do update
        set nickname = excluded.nickname;

    return target_pid;
end;
$$;

alter table public.app_user enable row level security;
alter table public.project enable row level security;
alter table public.project_member enable row level security;
alter table public.project_invite enable row level security;
alter table public.link enable row level security;
alter table public.task enable row level security;

create policy "Users can read their own profile"
    on public.app_user for select to authenticated
    using (uid = auth.uid());

create policy "Users can update their own profile"
    on public.app_user for update to authenticated
    using (uid = auth.uid())
    with check (uid = auth.uid());

create policy "Project members can read their projects"
    on public.project for select to authenticated
    using (owner_uid = auth.uid() or public.is_project_member(pid));

create policy "Owners can update their projects"
    on public.project for update to authenticated
    using (public.has_project_role(pid, array['owner']))
    with check (public.has_project_role(pid, array['owner']));

create policy "Owners can delete their projects"
    on public.project for delete to authenticated
    using (public.has_project_role(pid, array['owner']));

create policy "Members can read project membership"
    on public.project_member for select to authenticated
    using (public.is_project_member(pid));

create policy "Users can leave projects and admins can remove members"
    on public.project_member for delete to authenticated
    using (
        (uid = auth.uid() and role <> 'owner')
        or (uid <> auth.uid() and public.has_project_role(pid, array['owner', 'admin']))
    );

create policy "Members can read project links"
    on public.link for select to authenticated
    using (public.is_project_member(pid));

create policy "Members can add project links"
    on public.link for insert to authenticated
    with check (created_by_uid = auth.uid() and public.is_project_member(pid));

create policy "Authors and admins can update project links"
    on public.link for update to authenticated
    using (
        created_by_uid = auth.uid()
        or public.has_project_role(pid, array['owner', 'admin'])
    )
    with check (public.is_project_member(pid));

create policy "Authors and admins can delete project links"
    on public.link for delete to authenticated
    using (
        created_by_uid = auth.uid()
        or public.has_project_role(pid, array['owner', 'admin'])
    );

create policy "Members can read project tasks"
    on public.task for select to authenticated
    using (public.is_project_member(pid));

create policy "Members can create project tasks"
    on public.task for insert to authenticated
    with check (created_by_uid = auth.uid() and public.is_project_member(pid));

create policy "Members can update project tasks"
    on public.task for update to authenticated
    using (public.is_project_member(pid))
    with check (public.is_project_member(pid));

create policy "Creators and admins can delete project tasks"
    on public.task for delete to authenticated
    using (
        created_by_uid = auth.uid()
        or public.has_project_role(pid, array['owner', 'admin'])
    );

grant usage on schema public to authenticated;
grant select on public.app_user to authenticated;
grant update (display_name) on public.app_user to authenticated;
grant select, delete on public.project to authenticated;
grant update (title, description) on public.project to authenticated;
grant select, delete on public.project_member to authenticated;
grant select, insert, delete on public.link to authenticated;
grant update (url, source_platform) on public.link to authenticated;
grant select, insert, delete on public.task to authenticated;
grant update (title, description, status, due_at, assigned_uid) on public.task to authenticated;

revoke all on function public.handle_auth_user_change() from public, anon, authenticated;
revoke all on function public.is_project_member(uuid) from public, anon;
revoke all on function public.has_project_role(uuid, text[]) from public, anon;
revoke all on function public.create_project(text, text, text) from public, anon;
revoke all on function public.create_project_invite(uuid, timestamptz) from public, anon;
revoke all on function public.join_project_with_invite(text, text) from public, anon;

grant execute on function public.is_project_member(uuid) to authenticated;
grant execute on function public.has_project_role(uuid, text[]) to authenticated;
grant execute on function public.create_project(text, text, text) to authenticated;
grant execute on function public.create_project_invite(uuid, timestamptz) to authenticated;
grant execute on function public.join_project_with_invite(text, text) to authenticated;
