using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentStudio.Connector;

public sealed class ConnectorSessionStore
{
    public const string SessionCookieName = "agentstudio-connector-session";
    public const string CsrfCookieName = "agentstudio-csrf";
    public const string CsrfHeaderName = "X-CSRF-Token";

    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    public void Issue(HttpResponse response)
    {
        var session = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        var csrf = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();
        _sessions[session] = csrf;
        AppendCookies(response, session, csrf);
    }

    public bool ValidateSession(HttpRequest request, out string session)
    {
        session = request.Cookies[SessionCookieName] ?? string.Empty;
        return session.Length > 0 && _sessions.ContainsKey(session);
    }

    public bool ValidateCsrf(HttpRequest request, string session)
    {
        if (!_sessions.TryGetValue(session, out var expected)) return false;
        var cookie = request.Cookies[CsrfCookieName];
        var header = request.Headers[CsrfHeaderName].FirstOrDefault();
        return FixedEquals(expected, cookie) && FixedEquals(expected, header);
    }

    public void Logout(HttpRequest request, HttpResponse response)
    {
        var session = request.Cookies[SessionCookieName];
        if (!string.IsNullOrWhiteSpace(session)) _sessions.TryRemove(session, out _);
        DeleteCookies(response);
    }

    public void RotateAll() => _sessions.Clear();

    private static bool FixedEquals(string expected, string? supplied)
    {
        if (supplied is null) return false;
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void AppendCookies(HttpResponse response, string session, string csrf)
    {
        var sessionOptions = CookieOptions();
        sessionOptions.HttpOnly = true;
        response.Cookies.Append(SessionCookieName, session, sessionOptions);
        response.Cookies.Append(CsrfCookieName, csrf, CookieOptions());
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
}
