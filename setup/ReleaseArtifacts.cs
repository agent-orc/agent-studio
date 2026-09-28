using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;

namespace AgentStudio.Setup;

internal sealed class ReleaseArtifacts(
    string version,
    string? releaseDirectory,
    HttpClient? httpClient = null) : IAsyncDisposable
{
    private const string DefaultReleaseBase =
        "https://github.com/agent-orc/agent-studio/releases/download";
    private readonly HttpClient _http = httpClient ?? CreateHttpClient();
    private readonly bool _ownsHttp = httpClient is null;
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"agent-orchestrator-setup-{Guid.NewGuid():N}");
    private string? _checksums;

    public string Version { get; } = SetupOptions.NormalizeVersion(version)
                                    ?? throw new ArgumentException("Release version is required.");

    public static string CurrentVersion()
    {
        var informational = typeof(ReleaseArtifacts).Assembly
                                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                ?.InformationalVersion
                            ?? typeof(ReleaseArtifacts).Assembly.GetName().Version?.ToString(3)
                            ?? "0.1.0";
        return SetupOptions.NormalizeVersion(informational.Split('+')[0])
               ?? throw new InvalidOperationException(
                   "The setup executable does not carry a semantic release version.");
    }

    public async Task<string> ExtractOrchestratorAsync(CancellationToken cancellationToken)
        => await DownloadVerifyExtractAsync(
            $"agent-orchestrator-{Version}-linux-x64.tar.gz",
            $"agent-orchestrator-{Version}-linux-x64",
            cancellationToken);

    public async Task<string> ExtractHostAsync(CancellationToken cancellationToken)
        => await DownloadVerifyExtractAsync(
            $"agent-host-{Version}.tar.gz",
            $"agent-host-{Version}",
            cancellationToken);

    public async Task<string> ExtractStudioAsync(CancellationToken cancellationToken)
        => await DownloadVerifyExtractAsync(
            $"agent-studio-{Version}.tar.gz",
            $"agent-studio-{Version}",
            cancellationToken);

    public async Task<string> ExtractComposeAsync(CancellationToken cancellationToken)
        => await DownloadVerifyExtractAsync(
            $"agent-studio-compose-{Version}.tar.gz",
            $"agent-studio-compose-{Version}",
            cancellationToken);

    public async Task<string> ExtractWindowsPackageAsync(CancellationToken cancellationToken)
        => await DownloadVerifyExtractAsync(
            $"agent-orchestrator-{Version}-win-x64.zip",
            $"agent-orchestrator-{Version}-win-x64",
            cancellationToken);

    private async Task<string> DownloadVerifyExtractAsync(
        string archiveName,
        string expectedDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var archivePath = await GetFileAsync(archiveName, cancellationToken);
        var checksums = await GetChecksumsAsync(cancellationToken);
        var expectedHash = ParseExpectedHash(checksums, archiveName);
        await using (var stream = File.OpenRead(archivePath))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expectedHash)))
                throw new InvalidDataException(
                    $"SHA-256 verification failed for {archiveName}.");
        }

        Console.WriteLine($"  [ok] Verified {archiveName}");
        var extractionRoot = Path.Combine(_temporaryRoot, $"extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractionRoot);
        if (archiveName.EndsWith(".zip", StringComparison.Ordinal))
        {
            ZipFile.ExtractToDirectory(archivePath, extractionRoot, overwriteFiles: false);
        }
        else
        {
            await using var archive = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, extractionRoot, overwriteFiles: false, cancellationToken);
        }
        var result = Path.Combine(extractionRoot, expectedDirectory);
        if (!Directory.Exists(result))
            throw new InvalidDataException(
                $"{archiveName} has an unexpected directory layout.");
        ValidateReleaseMetadata(result);
        return result;
    }

    private async Task<string> GetFileAsync(string name, CancellationToken cancellationToken)
    {
        if (releaseDirectory is not null)
        {
            var local = Path.Combine(releaseDirectory, name);
            if (!File.Exists(local))
                throw new FileNotFoundException(
                    $"Offline release directory does not contain {name}.", local);
            return local;
        }

        var destination = Path.Combine(_temporaryRoot, name);
        if (File.Exists(destination))
            return destination;
        var releaseBase = Environment.GetEnvironmentVariable("AGENT_STUDIO_RELEASE_BASE_URL")
                          ?? DefaultReleaseBase;
        var url = $"{releaseBase.TrimEnd('/')}/v{Version}/{name}";
        Console.WriteLine($"  Downloading {name}");
        using var response = await _http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Release asset was not found: {url}");
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken);
        return destination;
    }

    private async Task<string> GetChecksumsAsync(CancellationToken cancellationToken)
    {
        if (_checksums is not null)
            return _checksums;
        var path = await GetFileAsync("SHA256SUMS", cancellationToken);
        _checksums = await File.ReadAllTextAsync(path, cancellationToken);
        return _checksums;
    }

    internal static string ParseExpectedHash(string content, string fileName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 2
                && string.Equals(fields[1].TrimStart('*'), fileName, StringComparison.Ordinal)
                && fields[0].Length == 64
                && fields[0].All(Uri.IsHexDigit))
                return fields[0].ToLowerInvariant();
        }
        throw new InvalidDataException($"SHA256SUMS does not name {fileName}.");
    }

    private void ValidateReleaseMetadata(string root)
    {
        var versionPath = Path.Combine(root, "VERSION");
        var releasePath = Path.Combine(root, "RELEASE");
        if (!File.Exists(versionPath) || !File.Exists(releasePath))
            throw new InvalidDataException(
                $"Release asset is missing VERSION or RELEASE metadata: {root}");
        var extractedVersion = SetupOptions.NormalizeVersion(
            File.ReadLines(versionPath).FirstOrDefault());
        if (!string.Equals(extractedVersion, Version, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Release metadata says {extractedVersion ?? "unknown"}, expected {Version}.");
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
            _http.Dispose();
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agent-orchestrator-setup/1");
        return client;
    }
}
