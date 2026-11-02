using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.CliHosting;
using AgentStudio.TaskServer.Contracts;
using Porta.Pty;

namespace AgentRunner;

/// <summary>Claims the Task Server's single subscription probe lease, then reads locally.</summary>
public sealed class HostQuotaPoller(TaskServerClient client, RunnerOptions options, Action<string> log)
{
    public async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var provider in new[] { "claude", "codex" })
            {
                try
                {
                    var key = HostQuotaIdentity.Digest(provider);
                    var claim = await client.ClaimHostQuotaAsync(
                        new(client.RunnerInstanceId, provider, key), ct);
                    if (claim is null) return; // Older Task Server or local compatibility profile.
                    if (!claim.Granted || claim.ProbeId is null) continue;
                    var binary = provider == "claude" ? options.ClaudeCliBin : options.CodexCliBin;
                    var reading = await HostQuotaReader.ReadAsync(provider, binary, ct);
                    await client.ReportHostQuotaAsync(new(
                        client.RunnerInstanceId, provider, key, claim.ProbeId,
                        reading.ObservedAt, reading.CliVersion, reading.Plan,
                        reading.Windows, reading.Error), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    // CLI output and credential data must never enter runtime logs.
                    log($"host quota {provider} transport failed: {exception.GetType().Name}");
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}

public static class HostQuotaIdentity
{
    public static string Digest(string provider)
    {
        var identity = provider switch
        {
            "codex" => CodexIdentity(),
            "claude" => ClaudeIdentity(),
            _ => null,
        };
        return DigestAccount(provider, identity);
    }

    public static string DigestAccount(string provider, string? identity)
    {
        var normalized = string.IsNullOrWhiteSpace(identity)
            ? "provider:" + provider
            : "account:" + provider + ":" + identity.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string? CodexIdentity()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
            var root = document.RootElement;
            if (root.TryGetProperty("tokens", out var tokens)
                && tokens.TryGetProperty("id_token", out var jwt))
            {
                var parts = jwt.GetString()?.Split('.');
                if (parts is { Length: >= 2 })
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                    using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                    if (claims.RootElement.TryGetProperty("sub", out var subject)
                        && subject.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(subject.GetString())) return subject.GetString();
                }
            }
            if (root.TryGetProperty("account_id", out var id)
                && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString();
        }
        catch (Exception) { }
        return null;
    }

    private static string? ClaudeIdentity()
    {
        var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new[]
        {
            Path.Combine(home, ".claude.json"),
            config is null ? "" : Path.Combine(config, "settings.json")
        };
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("oauthAccount", out var account))
                {
                    foreach (var field in new[] { "accountUuid", "emailAddress" })
                        if (account.TryGetProperty(field, out var value)
                            && value.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString();
                }
            }
            catch (Exception) { }
        }
        return null;
    }
}

public sealed record HostQuotaRead(DateTime ObservedAt, string? CliVersion, string? Plan,
    IReadOnlyList<HostQuotaWindowDto> Windows, string? Error);

public static class HostQuotaReader
{
    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)",
        RegexOptions.Compiled);
    private static readonly Regex ClaudeReady = new(@"for\s*shortcuts|esc\s*to\s*interrupt", RegexOptions.IgnoreCase);
    private static readonly Regex ClaudeUsage = new(@"Current\s*session|Current\s*week|Settings\s*Status\s*Config\s*Usage\s*Stats", RegexOptions.IgnoreCase);
    private static readonly Regex CodexReady = new(@"OpenAI\s*Codex|model:", RegexOptions.IgnoreCase);
    private static readonly Regex CodexStatus = new(@"5h\s*limit|Weekly\s*limit|Account:", RegexOptions.IgnoreCase);

    public static async Task<HostQuotaRead> ReadAsync(string provider, string binary,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(65));
        var ct = deadline.Token;
        try
        {
            var scratch = Path.Combine(Path.GetTempPath(), "agent-host-quota", provider);
            Directory.CreateDirectory(scratch);
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
            environment["TERM"] = "xterm-256color";
            environment["NO_COLOR"] = "1";
            environment["CLAUDE_CODE_DISABLE_AUTOUPDATER"] = "1";
            environment["DISABLE_AUTOUPDATER"] = "1";
            var connection = await PtyProvider.SpawnAsync(new PtyOptions
            {
                Name = "xterm-256color", Cols = 160, Rows = 40, Cwd = scratch,
                App = binary, CommandLine = [], Environment = environment
            }, ct);
            await using var session = new QuotaTerminal(connection);
            await session.IdleAsync(1500, 8000, ct);
            if (provider == "claude")
            {
                await session.SendIfAsync(@"trust\s*this\s*folder|Quick\s*safety\s*check", "1\r", 4000, ct);
                await session.SendIfAsync(@"Choose\s*the\s*text\s*style|match\s*terminal", "\r", 3500, ct);
                await session.SendIfAsync(@"fullscreen\s*renderer|Flicker-?free|What'?s\s*new", "\x1b", 3500, ct);
                await session.SendIfAsync(@"fullscreen\s*renderer|Flicker-?free|What'?s\s*new", "\x1b", 2500, ct);
                await session.WaitAsync(ClaudeReady, 10000, ct);
                await session.SendAsync("/usage\r", ct);
                await session.WaitAsync(ClaudeUsage, 8000, ct);
            }
            else
            {
                await session.SendIfAsync(@"trust\s*the\s*contents|Yes,\s*continue", "\r", 4000, ct);
                await session.SendIfAsync(@"Update\s*available|Skip\s*until\s*next\s*version", "\x1b[B\r", 4000, ct);
                await session.SendIfAsync(@"hooks?\s*need\s*review|Press\s*t\s*to\s*trust\s*all", "\x1b", 3000, ct);
                await session.WaitAsync(CodexReady, 10000, ct);
                await session.SendAsync("/status", ct);
                await Task.Delay(500, ct);
                await session.SendAsync("\r", ct);
                await session.WaitAsync(CodexStatus, 10000, ct);
            }
            await session.IdleAsync(1200, 6000, ct);
            var output = session.Snapshot();
            var observed = DateTime.UtcNow;
            var parsed = provider == "claude"
                ? SubscriptionQuotaParser.ParseClaude(output, observed)
                : SubscriptionQuotaParser.ParseCodex(output, observed);
            var plan = provider == "claude" ? ClaudePlan(output) : SubscriptionQuotaParser.CodexPlan(output);
            var versionMatch = provider == "claude"
                ? Regex.Match(output, @"Claude\s*Code\s*v?\s*(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)
                : Regex.Match(output, @"(?:OpenAI\s*)?Codex\s*(?:\(\s*)?v?\s*(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
            var version = versionMatch.Success ? versionMatch.Groups["version"].Value : null;
            var windows = parsed.Select(window => new HostQuotaWindowDto(
                window.Label, window.UsedPct, window.ResetAt, window.ResetLabel)).ToArray();
            var error = windows.Length == 0
                ? output.Contains("login", StringComparison.OrdinalIgnoreCase)
                    ? "Provider sign-in is unavailable."
                    : "Quota panel did not contain a parseable window."
                : null;
            return new(observed, version, plan, windows, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new(DateTime.UtcNow, null, null, [],
                exception is OperationCanceledException ? "Quota probe timed out."
                : exception is System.ComponentModel.Win32Exception or FileNotFoundException
                    ? "Provider CLI is unavailable on this host."
                    : "Host quota probe failed.");
        }
    }

    private static string? ClaudePlan(string output)
        => Regex.Match(output, @"Claude\s*(Pro\+?|Free|Team|Max(?:\s*\d+x)?|Enterprise)", RegexOptions.IgnoreCase)
            is { Success: true } plan ? plan.Groups[1].Value : null;

    private sealed class QuotaTerminal : IAsyncDisposable
    {
        private readonly IPtyConnection _connection;
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reader;
        private DateTime _lastByte = DateTime.UtcNow;

        public QuotaTerminal(IPtyConnection connection)
        {
            _connection = connection;
            _reader = Task.Run(ReadAsync);
        }

        private async Task ReadAsync()
        {
            var bytes = new byte[4096];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var count = await _connection.ReaderStream.ReadAsync(bytes, _stop.Token);
                    if (count <= 0) break;
                    lock (_gate)
                    {
                        _buffer.Append(Encoding.UTF8.GetString(bytes, 0, count));
                        if (_buffer.Length > 64 * 1024) _buffer.Remove(0, _buffer.Length - 64 * 1024);
                        _lastByte = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception) { }
        }

        public string Snapshot()
        {
            lock (_gate) return Ansi.Replace(_buffer.ToString(), "");
        }

        public async Task<bool> WaitAsync(Regex pattern, int timeoutMs, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (pattern.IsMatch(Snapshot())) return true;
                await Task.Delay(100, ct);
            }
            return false;
        }

        public async Task SendIfAsync(string pattern, string keys, int timeoutMs, CancellationToken ct)
        {
            if (await WaitAsync(new Regex(pattern, RegexOptions.IgnoreCase), timeoutMs, ct))
            {
                await SendAsync(keys, ct);
                await IdleAsync(800, 5000, ct);
            }
        }

        public async Task SendAsync(string keys, CancellationToken ct)
        {
            await _connection.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(keys), ct);
            await _connection.WriterStream.FlushAsync(ct);
        }

        public async Task IdleAsync(int idleMs, int timeoutMs, CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) if ((DateTime.UtcNow - _lastByte).TotalMilliseconds >= idleMs) return;
                await Task.Delay(75, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { _connection.Kill(); } catch (Exception) { }
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            _connection.Dispose();
            _stop.Dispose();
        }
    }
}
