using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Syncore.Services;

public sealed class SupabaseWorkspaceService(
    SupabaseAuthService supabaseAuth,
    SupabaseSessionStore sessions,
    AuthenticationStateProvider authenticationStateProvider,
    ILogger<SupabaseWorkspaceService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkspaceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var projects = await GetAsync<List<ProjectRow>>(
            "rest/v1/project?select=pid,title,description,owner_uid,created_at,project_member(uid,nickname,role),task(tid,title,description,status,due_at,created_by_uid,assigned_uid)&order=created_at.desc",
            cancellationToken) ?? [];

        var cards = projects.Select(MapProject).ToArray();
        var upcoming = cards.SelectMany(project => project.Tasks
                .Where(task => task.Status != WorkspaceTaskStatus.Done)
                .Select(task => new UpcomingWorkspaceTask(project.Id, project.Title, task)))
            .OrderBy(item => item.Task.DueAt)
            .ToArray();

        return new WorkspaceDashboard(
            cards,
            upcoming,
            await GetProfileAsync(cancellationToken));
    }

    public async Task<WorkspaceProject?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var rows = await GetAsync<List<ProjectRow>>(
            $"rest/v1/project?select=pid,title,description,owner_uid,created_at,project_member(uid,nickname,role),task(tid,title,description,status,due_at,created_by_uid,assigned_uid),link(lid,url,source_platform,created_by_uid,created_at)&pid=eq.{projectId:D}&limit=1",
            cancellationToken);

        if (rows is not { Count: > 0 })
        {
            return null;
        }

        var project = MapProject(rows[0]);
        var links = (rows[0].Links ?? [])
            .OrderByDescending(link => link.CreatedAt)
            .Select(link => new WorkspaceLink(
                link.Id,
                link.Url,
                string.IsNullOrWhiteSpace(link.SourcePlatform) ? "Website" : link.SourcePlatform,
                GetMemberName(rows[0].Members, link.CreatedByUserId),
                link.CreatedAt))
            .ToArray();

        return project with { Links = links };
    }

    public async Task<string> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        var rows = await GetAsync<List<ProfileRow>>(
            $"rest/v1/app_user?select=display_name&uid=eq.{Uri.EscapeDataString(userId)}&limit=1",
            cancellationToken);

        var name = rows?.FirstOrDefault()?.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? await GetEmailNameAsync(cancellationToken) : name;
    }

    public async Task<Guid> CreateProjectAsync(
        string title,
        string description,
        CancellationToken cancellationToken = default)
    {
        var displayName = await GetProfileAsync(cancellationToken);
        var result = await PostRpcAsync<Guid>(
            "create_project",
            new CreateProjectRpc(title.Trim(), description.Trim(), displayName),
            cancellationToken);
        return result;
    }

    public async Task<Guid> JoinProjectAsync(
        string inviteCode,
        string nickname,
        CancellationToken cancellationToken = default)
    {
        return await PostRpcAsync<Guid>(
            "join_project_with_invite",
            new JoinProjectRpc(inviteCode.Trim(), nickname.Trim()),
            cancellationToken);
    }

    public async Task<string> CreateInviteAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await PostRpcAsync<string>(
            "create_project_invite",
            new CreateInviteRpc(projectId),
            cancellationToken);
    }

    public async Task AddTaskAsync(
        Guid projectId,
        string title,
        string description,
        DateTime? dueDate,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        await SendAsync(
            HttpMethod.Post,
            "rest/v1/task",
            new CreateTaskRequest(
                projectId,
                title.Trim(),
                string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                "todo",
                dueDate is null ? null : new DateTimeOffset(DateTime.SpecifyKind(dueDate.Value, DateTimeKind.Local)),
                userId),
            cancellationToken);
    }

    public async Task UpdateTaskStatusAsync(
        Guid projectId,
        Guid taskId,
        WorkspaceTaskStatus status,
        CancellationToken cancellationToken = default)
    {
        var databaseStatus = status switch
        {
            WorkspaceTaskStatus.ToDo => "todo",
            WorkspaceTaskStatus.InProgress => "in_progress",
            WorkspaceTaskStatus.Done => "done",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

        await SendAsync(
            HttpMethod.Patch,
            $"rest/v1/task?pid=eq.{projectId:D}&tid=eq.{taskId:D}",
            new UpdateTaskRequest(databaseStatus),
            cancellationToken);
    }

    public async Task AddLinkAsync(
        Guid projectId,
        string url,
        string sourcePlatform,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        await SendAsync(
            HttpMethod.Post,
            "rest/v1/link",
            new CreateLinkRequest(
                projectId,
                url.Trim(),
                string.IsNullOrWhiteSpace(sourcePlatform) ? null : sourcePlatform.Trim(),
                userId),
            cancellationToken);
    }

    public async Task UpdateProfileAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        await SendAsync(
            HttpMethod.Patch,
            $"rest/v1/app_user?uid=eq.{Uri.EscapeDataString(userId)}",
            new UpdateProfileRequest(displayName.Trim()),
            cancellationToken);
    }

    public async Task<string> GetEmailAsync(CancellationToken cancellationToken = default)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.Email) ?? "";
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, path, null, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<T> PostRpcAsync<T>(
        string function,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            $"rest/v1/rpc/{function}",
            body,
            cancellationToken);

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        if (result is null)
        {
            throw new SupabaseRequestException("Supabase returned an empty result.");
        }

        return result;
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(method, path, body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        var sessionId = state.User.FindFirstValue("syncore_session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new SupabaseSessionExpiredException();
        }

        var session = await sessions.GetValidAsync(sessionId, supabaseAuth, cancellationToken);
        var client = supabaseAuth.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        logger.LogWarning(
            "Supabase REST request {Method} {Path} failed with status {StatusCode}.",
            method,
            path.Split('?')[0],
            response.StatusCode);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new SupabaseSessionExpiredException();
        }

        response.Dispose();
        throw response.StatusCode switch
        {
            HttpStatusCode.Forbidden => new SupabaseRequestException(
                "You don’t have permission to make that change.", response.StatusCode),
            HttpStatusCode.NotFound => new SupabaseRequestException(
                "That project or item could not be found.", response.StatusCode),
            HttpStatusCode.Conflict => new SupabaseRequestException(
                "That change conflicts with existing project data.", response.StatusCode),
            _ => new SupabaseRequestException(
                "We couldn’t complete that request. Check the Supabase database setup and try again.",
                response.StatusCode)
        };
    }

    private async Task<string> GetUserIdAsync(CancellationToken cancellationToken)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new SupabaseSessionExpiredException();
    }

    private async Task<string> GetEmailNameAsync(CancellationToken cancellationToken)
    {
        var email = await GetEmailAsync(cancellationToken);
        var separator = email.IndexOf('@');
        return separator > 0 ? email[..separator] : "Syncore member";
    }

    private static WorkspaceProject MapProject(ProjectRow row)
    {
        var members = row.Members ?? [];
        var ownerName = GetMemberName(members, row.OwnerUserId);
        var tasks = (row.Tasks ?? [])
            .OrderBy(task => task.DueAt)
            .Select(task => new WorkspaceTask(
                task.Id,
                task.Title,
                task.Description ?? "",
                ParseStatus(task.Status),
                task.DueAt,
                GetMemberName(members, task.AssignedUserId),
                GetMemberName(members, task.CreatedByUserId)))
            .ToArray();

        return new WorkspaceProject(
            row.Id,
            row.Title,
            row.Description ?? "",
            ownerName,
            row.CreatedAt,
            members.Select(member => new WorkspaceMember(member.UserId, member.Nickname, member.Role)).ToArray(),
            tasks,
            []);
    }

    private static string GetMemberName(IReadOnlyList<ProjectMemberRow>? members, string? userId)
    {
        var member = members?.FirstOrDefault(candidate => candidate.UserId == userId);
        return member?.Nickname ?? (string.IsNullOrWhiteSpace(userId) ? "Unassigned" : "Project member");
    }

    private static WorkspaceTaskStatus ParseStatus(string status) => status switch
    {
        "in_progress" => WorkspaceTaskStatus.InProgress,
        "done" => WorkspaceTaskStatus.Done,
        _ => WorkspaceTaskStatus.ToDo
    };

    private sealed record ProjectRow(
        [property: JsonPropertyName("pid")] Guid Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("owner_uid")] string OwnerUserId,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("project_member")] List<ProjectMemberRow>? Members,
        [property: JsonPropertyName("task")] List<TaskRow>? Tasks,
        [property: JsonPropertyName("link")] List<LinkRow>? Links);

    private sealed record ProjectMemberRow(
        [property: JsonPropertyName("uid")] string UserId,
        [property: JsonPropertyName("nickname")] string Nickname,
        [property: JsonPropertyName("role")] string Role);

    private sealed record TaskRow(
        [property: JsonPropertyName("tid")] Guid Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
        [property: JsonPropertyName("created_by_uid")] string CreatedByUserId,
        [property: JsonPropertyName("assigned_uid")] string? AssignedUserId);

    private sealed record LinkRow(
        [property: JsonPropertyName("lid")] Guid Id,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("source_platform")] string? SourcePlatform,
        [property: JsonPropertyName("created_by_uid")] string CreatedByUserId,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record ProfileRow(
        [property: JsonPropertyName("display_name")] string? DisplayName);

    private sealed record CreateProjectRpc(
        [property: JsonPropertyName("project_title")] string Title,
        [property: JsonPropertyName("project_description")] string Description,
        [property: JsonPropertyName("member_nickname")] string Nickname);

    private sealed record JoinProjectRpc(
        [property: JsonPropertyName("invite_code")] string Code,
        [property: JsonPropertyName("member_nickname")] string Nickname);

    private sealed record CreateInviteRpc(
        [property: JsonPropertyName("target_pid")] Guid ProjectId);

    private sealed record CreateTaskRequest(
        [property: JsonPropertyName("pid")] Guid ProjectId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt,
        [property: JsonPropertyName("created_by_uid")] string CreatedByUserId);

    private sealed record UpdateTaskRequest(
        [property: JsonPropertyName("status")] string Status);

    private sealed record CreateLinkRequest(
        [property: JsonPropertyName("pid")] Guid ProjectId,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("source_platform")] string? SourcePlatform,
        [property: JsonPropertyName("created_by_uid")] string CreatedByUserId);

    private sealed record UpdateProfileRequest(
        [property: JsonPropertyName("display_name")] string DisplayName);
}

public sealed record WorkspaceDashboard(
    IReadOnlyList<WorkspaceProject> Projects,
    IReadOnlyList<UpcomingWorkspaceTask> UpcomingTasks,
    string DisplayName);

public sealed record WorkspaceProject(
    Guid Id,
    string Title,
    string Description,
    string OwnerName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<WorkspaceMember> Members,
    IReadOnlyList<WorkspaceTask> Tasks,
    IReadOnlyList<WorkspaceLink> Links);

public sealed record WorkspaceMember(string UserId, string Nickname, string Role);

public sealed record WorkspaceTask(
    Guid Id,
    string Title,
    string Description,
    WorkspaceTaskStatus Status,
    DateTimeOffset? DueAt,
    string AssignedTo,
    string CreatedBy);

public sealed record WorkspaceLink(
    Guid Id,
    string Url,
    string Platform,
    string AddedBy,
    DateTimeOffset CreatedAt);

public sealed record UpcomingWorkspaceTask(Guid ProjectId, string ProjectTitle, WorkspaceTask Task);

public enum WorkspaceTaskStatus
{
    ToDo,
    InProgress,
    Done
}
