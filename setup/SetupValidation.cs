using System.Text.RegularExpressions;

namespace AgentStudio.Setup;

internal static partial class SetupValidation
{
    public static Uri RequireServerUrl(string value, bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Task Server URL must be an http(s) URL without credentials or a fragment.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && uri.IsLoopback))
            throw new ArgumentException(
                "A multi-machine Task Server URL must use HTTPS. Plain HTTP is accepted only on loopback.");
        return uri;
    }

    public static string RequireSimpleName(string value, string label)
    {
        var trimmed = value.Trim();
        if (!SimpleName().IsMatch(trimmed))
            throw new ArgumentException(
                $"{label} must start with a letter or digit and contain only letters, digits, '.', '_' or '-'.");
        return trimmed;
    }

    public static string RequireGitRemote(string value)
    {
        var trimmed = value.Trim();
        if (ScpGitRemote().IsMatch(trimmed))
            return trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme is not ("https" or "ssh")
                && !(uri.Scheme == "git" && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo) && uri.Scheme == "https")
            throw new ArgumentException(
                "Git remote must be a credential-free HTTPS or ssh:// URL, or a loopback git:// URL for a local repository.");
        return trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleName();

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._-]*@[A-Za-z0-9.-]+:[A-Za-z0-9._~/%+@:-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScpGitRemote();
}
