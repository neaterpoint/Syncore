using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Syncore.Services;

namespace Syncore.Endpoints;

public static class AuthEndpoints
{
    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth")
            .AddEndpointFilter<SameOriginFilter>()
            .RequireRateLimiting("auth");

        auth.MapPost("/otp", async (
            SendOtpRequest body,
            SupabaseAuthService supabase,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await supabase.SendOtpAsync(body.Email.Trim(), cancellationToken);
                return Results.Ok(new AuthActionResponse(true, "If the address can sign in, a one-time code has been sent."));
            }
            catch (InvalidOperationException exception)
            {
                loggerFactory.CreateLogger("SupabaseAuth").LogError(exception, "Supabase configuration is invalid.");
                return Results.Problem(
                    "Supabase sign-in is not configured on the server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (SupabaseRequestException exception)
            {
                var statusCode = GetClientStatusCode(exception.StatusCode);
                var logger = loggerFactory.CreateLogger("SupabaseAuth");
                if (statusCode == StatusCodes.Status503ServiceUnavailable)
                {
                    logger.LogError(
                        "Supabase OTP request failed with status {StatusCode}, code {ErrorCode}: {ProviderMessage}",
                        exception.StatusCode,
                        exception.ProviderErrorCode,
                        exception.ProviderMessage);
                }
                else
                {
                    logger.LogWarning(
                        "Supabase rejected an OTP request with status {StatusCode}, code {ErrorCode}: {ProviderMessage}",
                        exception.StatusCode,
                        exception.ProviderErrorCode,
                        exception.ProviderMessage);
                }

                return Results.Problem(
                    statusCode == StatusCodes.Status503ServiceUnavailable
                        ? "Email sign-in is temporarily unavailable. Check the server logs for the Supabase error details."
                        : statusCode == StatusCodes.Status429TooManyRequests
                            ? "Too many sign-in requests. Wait a minute and try again."
                            : "We couldn’t send a sign-in code. Check the email address and try again.",
                    statusCode: statusCode);
            }
            catch (HttpRequestException exception)
            {
                loggerFactory.CreateLogger("SupabaseAuth").LogError(exception, "Supabase Auth could not be reached.");
                return Results.Problem(
                    "Sign-in is temporarily unavailable. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        auth.MapPost("/verify", async (
            VerifyOtpRequest body,
            SupabaseAuthService supabase,
            SupabaseSessionStore sessions,
            HttpContext httpContext,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var tokens = await supabase.VerifyOtpAsync(body.Email.Trim(), body.Token.Trim(), cancellationToken);
                var sessionId = Guid.NewGuid().ToString("N");
                sessions.Add(sessionId, tokens);

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, tokens.User!.Id!),
                    new("syncore_session_id", sessionId)
                };

                if (!string.IsNullOrWhiteSpace(tokens.User.Email))
                {
                    claims.Add(new Claim(ClaimTypes.Email, tokens.User.Email));
                }

                var principal = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));

                await httpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        AllowRefresh = false,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                    });

                return Results.Ok(new AuthActionResponse(true, "Signed in."));
            }
            catch (InvalidOperationException exception)
            {
                loggerFactory.CreateLogger("SupabaseAuth").LogError(exception, "Supabase configuration is invalid.");
                return Results.Problem(
                    "Supabase sign-in is not configured on the server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (SupabaseRequestException exception)
            {
                var statusCode = GetClientStatusCode(exception.StatusCode);
                var logger = loggerFactory.CreateLogger("SupabaseAuth");
                if (statusCode == StatusCodes.Status503ServiceUnavailable)
                {
                    logger.LogError(
                        "Supabase OTP verification failed with status {StatusCode}, code {ErrorCode}: {ProviderMessage}",
                        exception.StatusCode,
                        exception.ProviderErrorCode,
                        exception.ProviderMessage);
                }
                else
                {
                    logger.LogWarning(
                        "Supabase rejected an OTP verification with status {StatusCode}, code {ErrorCode}: {ProviderMessage}",
                        exception.StatusCode,
                        exception.ProviderErrorCode,
                        exception.ProviderMessage);
                }

                return Results.Problem(
                    statusCode == StatusCodes.Status503ServiceUnavailable
                        ? "Email sign-in is temporarily unavailable. Check the server logs for the Supabase error details."
                        : statusCode == StatusCodes.Status429TooManyRequests
                            ? "Too many verification attempts. Wait a minute and try again."
                            : "That code is invalid or has expired. Request a new code and try again.",
                    statusCode: statusCode);
            }
            catch (HttpRequestException exception)
            {
                loggerFactory.CreateLogger("SupabaseAuth").LogError(exception, "Supabase Auth could not be reached.");
                return Results.Problem(
                    "Sign-in is temporarily unavailable. Try again shortly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        auth.MapPost("/logout", async (
            HttpContext httpContext,
            SupabaseSessionStore sessions) =>
        {
            var sessionId = httpContext.User.FindFirstValue("syncore_session_id");
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                sessions.Remove(sessionId);
            }

            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static int GetClientStatusCode(System.Net.HttpStatusCode? statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
        >= System.Net.HttpStatusCode.InternalServerError => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };

    public sealed class SameOriginFilter : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var request = context.HttpContext.Request;
            var origin = request.Headers.Origin;

            if (!string.IsNullOrWhiteSpace(origin) &&
                (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                 !string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return ValueTask.FromResult<object?>(Results.Problem(
                    "Cross-origin requests are not allowed.",
                    statusCode: StatusCodes.Status403Forbidden));
            }

            return next(context);
        }
    }
}

/// <summary>Request to send an email sign-in code.</summary>
public sealed record SendOtpRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Request to verify an email sign-in code.</summary>
public sealed record VerifyOtpRequest
{
    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; init; } = string.Empty;

    [Required, RegularExpression("^[0-9]{6}$")]
    public string Token { get; init; } = string.Empty;
}

/// <summary>Result of an authentication action.</summary>
public sealed record AuthActionResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message);
