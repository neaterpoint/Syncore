using System.Collections.Concurrent;

namespace Syncore.Services;

public sealed class SupabaseSessionStore
{
    private readonly ConcurrentDictionary<string, SessionEntry> sessions = new();

    public bool Contains(string id) => sessions.ContainsKey(id);

    public void Add(string id, SupabaseTokenResponse tokens)
    {
        sessions[id] = new SessionEntry(
            tokens.AccessToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn));
    }

    public async Task<SupabaseSession> GetValidAsync(
        string id,
        SupabaseAuthService auth,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(id, out var currentEntry))
        {
            throw new SupabaseSessionExpiredException();
        }

        var refreshLock = currentEntry.RefreshLock;
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!sessions.TryGetValue(id, out var entry))
            {
                throw new SupabaseSessionExpiredException();
            }

            if (entry.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return entry.ToSession();
            }

            SupabaseTokenResponse tokens;
            try
            {
                tokens = await auth.RefreshAsync(entry.RefreshToken, cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SupabaseRequestException)
            {
                sessions.TryRemove(id, out _);
                throw new SupabaseSessionExpiredException();
            }

            entry.AccessToken = tokens.AccessToken;
            entry.RefreshToken = tokens.RefreshToken;
            entry.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn);
            return entry.ToSession();
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public void Remove(string id)
    {
        sessions.TryRemove(id, out _);
    }

    private sealed class SessionEntry(string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        public string AccessToken { get; set; } = accessToken;
        public string RefreshToken { get; set; } = refreshToken;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public SemaphoreSlim RefreshLock { get; } = new(1, 1);

        public SupabaseSession ToSession() => new(AccessToken, RefreshToken, ExpiresAt);
    }
}

public sealed record SupabaseSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed class SupabaseSessionExpiredException : Exception
{
}
