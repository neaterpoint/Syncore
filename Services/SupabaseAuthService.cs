using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Syncore.Services;

public sealed class SupabaseAuthService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    public async Task SendOtpAsync(string email, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var response = await client.PostAsJsonAsync(
            "auth/v1/otp",
            new SupabaseSendOtpRequest(email, CreateUser: true),
            cancellationToken);
        await EnsureSuccessAsync(response, "We couldn’t send a sign-in code right now.", cancellationToken);
    }

    public async Task<SupabaseTokenResponse> VerifyOtpAsync(
        string email,
        string token,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var response = await client.PostAsJsonAsync(
            "auth/v1/verify",
            new SupabaseVerifyOtpRequest(email, token, "email"),
            cancellationToken);

        await EnsureSuccessAsync(response, "That code is invalid or has expired.", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SupabaseTokenResponse>(
            cancellationToken: cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken) ||
            string.IsNullOrWhiteSpace(result.RefreshToken) || result.User?.Id is null)
        {
            throw new SupabaseRequestException("Supabase returned an incomplete sign-in response.");
        }

        return result;
    }

    public async Task<SupabaseTokenResponse> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var client = CreateClient();
        using var response = await client.PostAsJsonAsync(
            "auth/v1/token?grant_type=refresh_token",
            new SupabaseRefreshTokenRequest(refreshToken),
            cancellationToken);

        await EnsureSuccessAsync(response, "Your sign-in session has expired.", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SupabaseTokenResponse>(
            cancellationToken: cancellationToken);

        if (result is null || string.IsNullOrWhiteSpace(result.AccessToken) ||
            string.IsNullOrWhiteSpace(result.RefreshToken))
        {
            throw new SupabaseRequestException("Supabase returned an incomplete refreshed session.");
        }

        return result;
    }

    internal SupabaseOptions Options => SupabaseOptions.FromConfiguration(configuration);

    internal HttpClient CreateClient()
    {
        var options = Options;
        var client = httpClientFactory.CreateClient("Supabase");
        client.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("apikey", options.PublishableKey);
        return client;
    }

    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallbackMessage,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? providerErrorCode = null;
        string? providerMessage = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                providerErrorCode = GetErrorValue(document.RootElement, "code", "error_code");
                providerMessage = GetErrorValue(document.RootElement, "message", "msg", "error_description", "error");
            }
        }
        catch (JsonException)
        {
            // Supabase may return an empty or non-JSON error body.
        }

        throw new SupabaseRequestException(fallbackMessage, response.StatusCode, providerErrorCode, providerMessage);
    }

    private static string? GetErrorValue(JsonElement body, params string[] names)
    {
        foreach (var name in names)
        {
            if (body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = new string((value.GetString() ?? string.Empty)
                    .Where(character => !char.IsControl(character))
                    .Take(256)
                    .ToArray());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }
}

public sealed record SupabaseOptions(string Url, string PublishableKey)
{
    public static SupabaseOptions FromConfiguration(IConfiguration configuration)
    {
        var url = configuration["Supabase:Url"];
        var key = configuration["Supabase:PublishableKey"];

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(key) ||
            key.Equals("YOUR_PUBLISHABLE_KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Supabase settings are missing or invalid. Configure Supabase:Url and Supabase:PublishableKey using .NET User Secrets.");
        }

        return new SupabaseOptions(uri.ToString().TrimEnd('/'), key);
    }
}

public sealed record SupabaseTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("user")] SupabaseUser? User);

public sealed record SupabaseUser(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("email")] string? Email);

public sealed record SupabaseSendOtpRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("create_user")] bool CreateUser);

public sealed record SupabaseVerifyOtpRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("type")] string Type);

public sealed record SupabaseRefreshTokenRequest(
    [property: JsonPropertyName("refresh_token")] string RefreshToken);

public sealed class SupabaseRequestException(
    string message,
    HttpStatusCode? statusCode = null,
    string? providerErrorCode = null,
    string? providerMessage = null)
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProviderErrorCode { get; } = providerErrorCode;
    public string? ProviderMessage { get; } = providerMessage;
}
