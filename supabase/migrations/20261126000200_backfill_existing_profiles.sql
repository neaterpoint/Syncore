insert into public.app_user (uid, email, display_name)
select
    auth_user.id,
    auth_user.email,
    nullif(btrim(auth_user.raw_user_meta_data ->> 'display_name'), '')
from auth.users as auth_user
on conflict (uid) do update
set email = excluded.email;
