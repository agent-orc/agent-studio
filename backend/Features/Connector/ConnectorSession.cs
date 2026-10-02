using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentStudio.Connector;

/// <summary>
/// Connector-issued browser sessions. Each session binds one random CSRF
/// token; a mutation must present the session cookie, the matching CSRF
/// cookie, and the same token in <see cref="CsrfHeaderName"/>. A token is
/// therefore useless with any other session, after logout, after an upstream
/// switch, and after the session's absolute lifetime ends.
/// </summary>
public sealed class ConnectorSessionStore(TimeSpan lifetime, TimeProvider time)
{
    public const string SessionCookieName = "agentstudio-connector-session";
    public const string CsrfCookieName = "agentstudio-csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";
    private const int PruneThreshold = 256;

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);

    public ConnectorSessionStore()
        : this(ConnectorOptions.DefaultSessionLifetime, TimeProvider.System)
    {
    }

    public void Issue(HttpResponse response)
    {
        if (_sessions.Count >= PruneThreshold) PruneExpired();
        var session = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var csrf = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        _sessions[session] = new SessionEntry(csrf, time.GetUtcNow() + lifetime);
        AppendCookies(response, session, csrf);
    }

    public bool ValidateSession(HttpRequest request, out string session)
    {
        session = request.Cookies[SessionCookieName] ?? string.Empty;
        if (session.Length == 0 || !_sessions.TryGetValue(session, out var entry)) return false;
        if (entry.ExpiresAtUtc > time.GetUtcNow()) return true;
        _sessions.TryRemove(session, out _);
        return false;
    }

    public bool ValidateCsrf(HttpRequest request, string session)
    {
        if (!_sessions.TryGetValue(session, out var entry)) return false;
        var cookie = request.Cookies[CsrfCookieName];
        var header = request.Headers[CsrfHeaderName].FirstOrDefault();
        return FixedEquals(entry.Csrf, cookie) && FixedEquals(entry.Csrf, header);
    }

    public void Logout(HttpRequest request, HttpResponse response)
    {
        var session = request.Cookies[SessionCookieName];
        if (!string.IsNullOrWhiteSpace(session)) _sessions.TryRemove(session, out _);
        DeleteCookies(response);
    }

    public void RotateAll() => _sessions.Clear();

    private void PruneExpired()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _sessions)
        {
            if (entry.ExpiresAtUtc <= now) _sessions.TryRemove(key, out _);
        }
    }

    private static bool FixedEquals(string expected, string? supplied)
    {
        if (supplied is null) return false;
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void AppendCookies(HttpResponse response, string session, string csrf)
    {
        var sessionOptions = CookieOptions();
        sessionOptions.HttpOnly = true;
        sessionOptions.MaxAge = lifetime;
        response.Cookies.Append(SessionCookieName, session, sessionOptions);
        var csrfOptions = CookieOptions();
        csrfOptions.MaxAge = lifetime;
        response.Cookies.Append(CsrfCookieName, csrf, csrfOptions);
    }

    private static void DeleteCookies(HttpResponse response)
    {
        var sessionOptions = CookieOptions();
        sessionOptions.HttpOnly = true;
        response.Cookies.Delete(SessionCookieName, sessionOptions);
        response.Cookies.Delete(CsrfCookieName, CookieOptions());
    }

    private static CookieOptions CookieOptions() => new()
    {
        Path = "/",
        SameSite = SameSiteMode.Strict,
        Secure = false,
        IsEssential = true,
    };

    private sealed record SessionEntry(string Csrf, DateTimeOffset ExpiresAtUtc);
}
