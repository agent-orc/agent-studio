using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Secrets;

/// <summary>1Password service-account item access through the official op CLI.</summary>
public sealed class OnePasswordBackend(SecretAdapterConfig config, IOnePasswordCommandClient? commandClient = null,
    Func<string, string?>? readCredential = null) : IExternalSecretBackend
{
    private readonly IOnePasswordCommandClient _commands = commandClient ?? new OnePasswordCli();
    public string Kind => "1password";

    public async Task ProbeAsync(CancellationToken ct)
        => await RunAsync(["vault", "get", config.Vault!, "--format=json"], null, ct);

    public async Task<int> WriteAsync(SecretScope scope, string name, ReadOnlyMemory<byte> value, int previousVersion, CancellationToken ct)
    {
        var title = Title(scope, name);
        var field = new { id = "agentStudioValue", label = "value", type = "CONCEALED", value = Encoding.UTF8.GetString(value.Span) };
        string output;
        if (previousVersion == 0)
        {
            var input = JsonSerializer.Serialize(new { title, category = "SECURE_NOTE", fields = new[] { field } });
            output = await RunAsync(["item", "create", "-", "--vault", config.Vault!, "--format=json"], input, ct);
        }
        else
        {
            using var existing = await GetAsync(title, ct);
            if (existing.RootElement.GetProperty("version").GetInt32() != previousVersion)
                throw new SecretAdapterException("adapter-version-conflict", "The 1Password item changed outside Agent Studio.");
            var root = System.Text.Json.Nodes.JsonNode.Parse(existing.RootElement.GetRawText())!.AsObject();
            var fields = root["fields"]!.AsArray();
            var current = fields.FirstOrDefault(item => item?["id"]?.GetValue<string>() == "agentStudioValue");
            if (current is null)
                throw new SecretAdapterException("adapter-value-missing", "The 1Password item has no Agent Studio value field.");
            current["value"] = field.value;
            output = await RunAsync(["item", "edit", title, "--vault", config.Vault!, "--format=json"], root.ToJsonString(), ct);
        }
        using var result = JsonDocument.Parse(output);
        var version = result.RootElement.GetProperty("version").GetInt32();
        if (version != previousVersion + 1)
            throw new SecretAdapterException("adapter-version-conflict", "1Password returned an unexpected item version.");
        return version;
    }

    public async Task<byte[]> ReadAsync(SecretScope scope, string name, int version, CancellationToken ct)
    {
        using var item = await GetAsync(Title(scope, name), ct);
        if (item.RootElement.GetProperty("version").GetInt32() != version)
            throw new SecretAdapterException("adapter-version-unsupported", "1Password service accounts can only resolve the current item version.");
        var field = item.RootElement.GetProperty("fields").EnumerateArray()
            .FirstOrDefault(entry => entry.TryGetProperty("id", out var id) && id.GetString() == "agentStudioValue");
        if (field.ValueKind == JsonValueKind.Undefined || !field.TryGetProperty("value", out var value))
            throw new SecretAdapterException("adapter-value-missing", "The 1Password item has no Agent Studio value field.");
        return Encoding.UTF8.GetBytes(value.GetString() ?? string.Empty);
    }

    public Task RevokeAsync(SecretScope scope, string name, IReadOnlyCollection<int> versions, CancellationToken ct)
        => throw new SecretAdapterException("adapter-revoke-unsupported", "1Password service accounts cannot revoke an individual item version. Delete the item or replace its value.");

    public async Task DeleteAsync(SecretScope scope, string name, CancellationToken ct)
        => await RunAsync(["item", "delete", Title(scope, name), "--vault", config.Vault!], null, ct);

    private async Task<JsonDocument> GetAsync(string title, CancellationToken ct)
        => JsonDocument.Parse(await RunAsync(["item", "get", title, "--vault", config.Vault!, "--format=json"], null, ct));

    private static string Title(SecretScope scope, string name)
        => "AgentStudio-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.Key.ToLowerInvariant() + "\n" + name)));

    private Task<string> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken ct)
        => _commands.RunAsync(arguments, input, SecretAdapterCredentials.Require(config.CredentialEnvironment, readCredential), ct);
}

public interface IOnePasswordCommandClient
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, string? input, string token, CancellationToken ct);
}

public sealed class OnePasswordCli(string executable = "op") : IOnePasswordCommandClient
{
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, string? input, string token, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["OP_SERVICE_ACCOUNT_TOKEN"] = token;
        try
        {
            using var process = Process.Start(start)
                ?? throw new SecretAdapterException("adapter-unreachable", "The 1Password CLI could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            var result = await stdout;
            await stderr; // Drain without exposing potentially sensitive CLI diagnostics.
            if (process.ExitCode != 0)
                throw new SecretAdapterException("adapter-cli-failed", "1Password refused the item operation; check service account permissions and item state.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SecretAdapterException("adapter-timeout", "1Password did not respond within 30 seconds.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SecretAdapterException("adapter-cli-missing", "Install the 1Password CLI on the backend host.");
        }
    }
}
