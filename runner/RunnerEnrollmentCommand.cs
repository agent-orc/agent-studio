using System.Net.Http.Json;
using System.Text.Json;

namespace AgentRunner;

/// <summary>Exchanges an owner-issued code over trusted HTTPS and saves the host identity.</summary>
internal static class RunnerEnrollmentCommand
{
    internal static async Task<int> RunAsync(
        string installationUrl,
        string code,
        TextWriter output,
        TextWriter error,
        CancellationToken ct,
        HttpClient? client = null,
        string? configDirectory = null)
    {
        if (!Uri.TryCreate(installationUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(code))
        {
            error.WriteLine("Enrollment requires an HTTPS installation URL and a one-time code.");
            return 2;
        }

        var directory = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent-host");
        var credentialPath = Path.Combine(directory, "runner.token");
        var environmentPath = Path.Combine(directory, "installation.env");
        if (File.Exists(credentialPath) || File.Exists(environmentPath))
        {
            error.WriteLine("Enrollment files already exist. Move them aside before enrolling this host again.");
            return 2;
        }

        using var ownedClient = client is null ? new HttpClient() : null;
        var http = client ?? ownedClient!;
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Default HttpClient validates the certificate chain with the host's
            // system trust store. Private installations must install their CA first.
            using var response = await http.PostAsJsonAsync(
                new Uri(uri, "/api/auth/runner-enroll"), new { code }, ct);
            if (!response.IsSuccessStatusCode)
            {
                error.WriteLine($"Enrollment was refused (HTTP {(int)response.StatusCode}).");
                return 4;
            }
            var enrolled = await response.Content.ReadFromJsonAsync<EnrolledIdentity>(cancellationToken: ct);
            if (enrolled is null || string.IsNullOrWhiteSpace(enrolled.RunnerId)
                || !enrolled.RunnerId.StartsWith("runner_", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(enrolled.RunnerName)
                || string.IsNullOrWhiteSpace(enrolled.Secret))
            {
                error.WriteLine("Enrollment returned an invalid runner identity.");
                return 4;
            }
            var displayName = EscapeEnvironmentValue(enrolled.RunnerName);

            await WritePrivateAsync(credentialPath, enrolled.Secret + Environment.NewLine, ct);
            try
            {
                await WritePrivateAsync(environmentPath,
                    $"RUNNER_SERVER_URL={uri.GetLeftPart(UriPartial.Path).TrimEnd('/')}\n"
                    + $"RUNNER_ID={enrolled.RunnerId}\n"
                    + $"RUNNER_NAME={displayName}\n"
                    + $"RUNNER_AUTH_TOKEN_FILE={credentialPath}\n", ct);
            }
            catch
            {
                File.Delete(credentialPath);
                throw;
            }
            output.WriteLine($"Enrolled {enrolled.RunnerName} as {enrolled.RunnerId}.");
            output.WriteLine($"Load {environmentPath} in the host service environment. TLS uses the system trust store.");
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error.WriteLine($"Enrollment failed: {exception.GetType().Name}. Verify HTTPS trust and the host file permissions.");
            return 4;
        }
    }

    private static async Task WritePrivateAsync(string path, string content, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    private static string EscapeEnvironmentValue(string value)
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('"') || value.Contains('\\'))
            throw new InvalidDataException("Runner display name cannot be written to the environment file.");
        return $"\"{value}\"";
    }

    private sealed record EnrolledIdentity(string RunnerId, string RunnerName, string Secret);
}
