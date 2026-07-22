using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using SaaSApp.ActivityLog.Application;
using SaaSApp.ActivityLog.Application.Contracts;
using SaaSApp.ActivityLog.Infrastructure.Options;
using SaaSApp.ActivityLog.Infrastructure.Services;
using SaaSApp.Logging;
using SaaSApp.MultiTenancy;

namespace SaaSApp.Api.Middleware;

/// <summary>
/// Writes Event Log rows for authenticated API completions (and Auth login routes).
/// Uses Response.OnCompleted so StatusCode matches the client response.
/// Event Log failures never affect the API response.
/// </summary>
public sealed class ApiEventLoggingMiddleware
{
    private static readonly string[] BodyPeekPrefixes =
    [
        "/api/users",
        "/api/workflows",
        "/api/workflow",
        "/api/form",
        "/api/repositories",
        "/api/auth/"
    ];

    private const string LoginAccessTokenItemKey = "EventLog.LoginAccessToken";

    private readonly RequestDelegate _next;
    private readonly EventLogOptions _options;

    public ApiEventLoggingMiddleware(RequestDelegate next, IOptions<EventLogOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantProvider tenantProvider,
        ITenantConnectionProvider connectionProvider,
        IEventLogWriter writer)
    {
        if (!_options.Enabled || !ShouldConsiderPath(context.Request.Path, context.Request.Method))
        {
            await _next(context);
            return;
        }

        Stream? originalBody = null;
        MemoryStream? responseBuffer = null;
        var loggingArmed = false;

        try
        {
            var tenantId = tenantProvider.GetTenantId();
            var connectionString = connectionProvider.ConnectionString;
            var path = context.Request.Path.Value ?? string.Empty;
            var method = context.Request.Method;
            var isAuthLogin = EventLogRouteMapper.IsAuthLoginRoute(path);

            var subject = EventLogSubject.Empty;
            if (ShouldPeekBody(method, path))
                subject = await PeekSubjectAsync(context);

            subject = await EnrichSubjectFromDbAsync(subject, method, path, connectionString, context.RequestAborted);

            if (isAuthLogin)
            {
                originalBody = context.Response.Body;
                responseBuffer = new MemoryStream();
                context.Response.Body = responseBuffer;
            }

            // Capture enriched subject for OnCompleted (local copy).
            var subjectForLog = subject;

            context.Response.OnCompleted(() =>
            {
                try
                {
                    if (tenantId == null || string.IsNullOrEmpty(connectionString))
                        return Task.CompletedTask;

                    var statusCode = ResolveStatusCode(context);
                    var isAuthenticated = context.User.Identity?.IsAuthenticated == true;
                    var shouldLog = isAuthenticated
                        || (isAuthLogin && _options.LogAuthLoginUnauthenticated);

                    if (!shouldLog)
                        return Task.CompletedTask;

                    if (!EventLogRouteMapper.ShouldLog(method, path))
                        return Task.CompletedTask;

                    context.Items.TryGetValue(LoginAccessTokenItemKey, out var tokenObj);
                    var loginAccessToken = tokenObj as string;

                    var actor = ResolveActor(context, isAuthLogin, statusCode, subjectForLog, loginAccessToken);
                    var mapped = EventLogRouteMapper.Map(
                        method,
                        path,
                        statusCode,
                        subjectForLog.With(email: actor.Email));

                    context.Items.TryGetValue(CorrelationIdMiddleware.CorrelationIdItemKey, out var correlationObj);

                    var entry = new EventLogEntry(
                        Guid.NewGuid(),
                        tenantId.Value,
                        actor.UserId,
                        actor.DisplayName,
                        actor.Email,
                        mapped.EventTitle,
                        mapped.EventType,
                        mapped.Category,
                        mapped.Severity,
                        GetClientIp(context),
                        method,
                        path,
                        statusCode,
                        correlationObj as string,
                        DateTime.UtcNow);

                    writer.Enqueue(entry, connectionString);
                }
                catch
                {
                    // Never break response completion.
                }

                return Task.CompletedTask;
            });

            loggingArmed = true;
        }
        catch
        {
            // Event Log setup failed — still process the request without logging.
            if (responseBuffer != null && originalBody != null)
            {
                context.Response.Body = originalBody;
                await responseBuffer.DisposeAsync();
                responseBuffer = null;
                originalBody = null;
            }
        }

        try
        {
            await _next(context);
        }
        finally
        {
            if (responseBuffer != null && originalBody != null)
            {
                try
                {
                    responseBuffer.Position = 0;
                    if (loggingArmed && context.Response.StatusCode is >= 200 and < 300)
                    {
                        var token = TryReadAccessToken(responseBuffer);
                        if (!string.IsNullOrWhiteSpace(token))
                            context.Items[LoginAccessTokenItemKey] = token;
                    }

                    responseBuffer.Position = 0;
                    await responseBuffer.CopyToAsync(originalBody);
                }
                catch
                {
                    // Best-effort restore.
                }
                finally
                {
                    context.Response.Body = originalBody;
                    await responseBuffer.DisposeAsync();
                }
            }
        }
    }

    private static async Task<EventLogSubject> EnrichSubjectFromDbAsync(
        EventLogSubject subject,
        string method,
        string path,
        string? connectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return subject;

            if ((HttpMethods.IsPut(method) || HttpMethods.IsDelete(method))
                && EventLogRouteMapper.TryGetUserIdFromPath(path, out var userId)
                && (string.IsNullOrWhiteSpace(subject.Name) || string.IsNullOrWhiteSpace(subject.Email)))
            {
                var (displayName, email) = await EventLogActorLookup.TryGetUserAsync(
                    connectionString, userId, cancellationToken);
                subject = subject.With(name: displayName, email: email);
            }

            if (HttpMethods.IsPut(method)
                && EventLogRouteMapper.TryGetRoleIdFromPath(path, out var roleId)
                && string.IsNullOrWhiteSpace(subject.RoleName))
            {
                var roleName = await EventLogActorLookup.TryGetRoleNameAsync(
                    connectionString, roleId, cancellationToken);
                subject = subject.With(roleName: roleName);
            }
        }
        catch
        {
            // ignore lookup failures
        }

        return subject;
    }

    private bool ShouldConsiderPath(PathString path, string method)
    {
        var value = path.Value ?? string.Empty;
        if (!value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsOptions(method) || HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            return false;

        if (value.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TenantSignupOtpPathHelper.MatchesPath(value))
            return false;

        foreach (var prefix in _options.ExcludedPathPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && value.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var prefix in _options.PublicPathPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && value.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool ShouldPeekBody(string method, string path)
    {
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) && !HttpMethods.IsPatch(method))
            return false;

        foreach (var prefix in BodyPeekPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task<EventLogSubject> PeekSubjectAsync(HttpContext context)
    {
        try
        {
            var request = context.Request;
            if (request.HasFormContentType)
            {
                string? fileName = null;
                if (request.Form.Files.Count > 0)
                    fileName = request.Form.Files[0].FileName;

                string? email = TryForm(request, "email", "Email");
                string? name = TryForm(request, "displayName", "DisplayName")
                    ?? TryForm(request, "name", "Name");
                string? roleName = TryForm(request, "roleName", "RoleName");
                string? role = TryForm(request, "role", "Role");
                string? groupName = TryForm(request, "groupName", "GroupName");

                if (!string.IsNullOrWhiteSpace(fileName)
                    || !string.IsNullOrWhiteSpace(email)
                    || !string.IsNullOrWhiteSpace(name)
                    || !string.IsNullOrWhiteSpace(roleName)
                    || !string.IsNullOrWhiteSpace(role)
                    || !string.IsNullOrWhiteSpace(groupName))
                {
                    return new EventLogSubject
                    {
                        FileName = NullIfWhite(fileName),
                        Email = NullIfWhite(email),
                        Name = NullIfWhite(name),
                        RoleName = NullIfWhite(roleName),
                        Role = NullIfWhite(role),
                        GroupName = NullIfWhite(groupName)
                    };
                }
            }

            if (request.ContentLength is null or 0)
                return EventLogSubject.Empty;

            request.EnableBuffering();
            request.Body.Position = 0;

            const int maxBytes = 64 * 1024;
            var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(request.ContentLength.Value, maxBytes))];
            var read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length));
            request.Body.Position = 0;

            if (read <= 0)
                return EventLogSubject.Empty;

            return EventLogSubject.ParseAllowlistedJson(buffer.AsSpan(0, read));
        }
        catch
        {
            try { context.Request.Body.Position = 0; } catch { /* ignore */ }
            return EventLogSubject.Empty;
        }
    }

    private static string? TryForm(HttpRequest request, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (request.Form.TryGetValue(key, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        return null;
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? TryReadAccessToken(MemoryStream responseBuffer)
    {
        try
        {
            if (responseBuffer.Length == 0)
                return null;

            using var doc = JsonDocument.Parse(responseBuffer.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("accessToken", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var token = prop.Value.GetString();
                    return string.IsNullOrWhiteSpace(token) ? null : token;
                }
            }
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static (Guid? UserId, string? Email, string? DisplayName) ResolveActor(
        HttpContext context,
        bool isAuthLogin,
        int statusCode,
        EventLogSubject subject,
        string? loginAccessToken)
    {
        Guid? userId = null;
        string? email = null;
        string? displayName = null;

        if (isAuthLogin && statusCode is >= 200 and < 300 && !string.IsNullOrWhiteSpace(loginAccessToken))
        {
            try
            {
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(loginAccessToken);
                userId = TryParseGuid(
                    jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value
                    ?? jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value);
                email = FirstNonWhiteSpace(
                    jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email)?.Value,
                    jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value);
                displayName = FirstNonWhiteSpace(
                    jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value,
                    jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value);
            }
            catch
            {
                // fall through
            }
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            userId ??= GetUserId(context.User);
            email = FirstNonWhiteSpace(email, GetUserEmail(context.User));
            displayName = FirstNonWhiteSpace(displayName, GetUserDisplayName(context.User));
        }

        email = FirstNonWhiteSpace(email, subject.Email);
        displayName = FirstNonWhiteSpace(displayName, subject.Name, email);

        return (userId, email, displayName);
    }

    private static int ResolveStatusCode(HttpContext context)
    {
        var statusCode = context.Response.StatusCode;
        if (statusCode >= 400)
            return statusCode;

        var errorFeature = context.Features.Get<IExceptionHandlerFeature>();
        if (errorFeature?.Error != null)
            return StatusCodes.Status500InternalServerError;

        return statusCode;
    }

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("userId")
            ?? user.FindFirstValue("oid");
        return TryParseGuid(sub);
    }

    private static string? GetUserEmail(ClaimsPrincipal user) =>
        FirstNonWhiteSpace(
            user.FindFirstValue("email"),
            user.FindFirstValue(ClaimTypes.Email),
            user.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"));

    private static string? GetUserDisplayName(ClaimsPrincipal user) =>
        FirstNonWhiteSpace(
            user.FindFirstValue("name"),
            user.FindFirstValue(ClaimTypes.Name),
            user.Identity?.Name);

    private static string? GetClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static Guid? TryParseGuid(string? value) =>
        Guid.TryParse(value, out var id) ? id : null;

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
