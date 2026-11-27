using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Tests.Architecture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AppendOnlyRegistryMergeTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "registry-merge-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData("drive-to-conclusion", false)]
    [InlineData("drive-to-conclusion", true)]
    [InlineData("security-overview", false)]
    [InlineData("security-overview", true)]
    [InlineData("operator-agent", false)]
    [InlineData("operator-agent", true)]
    public void Concurrent_entries_merge_through_the_individual_mechanical_stage(string feature, bool atStart)
    {
        var (rule, original) = Seed(feature);
        var integrationEntry = Entry(feature, "integration");
        var deliveryEntry = Entry(feature, "delivery");
        Git("switch", "-qc", "delivery");
        Write(rule.Path, InsertEntry(original, rule, deliveryEntry, atStart));
        Commit("AGT-2: add delivery entry");
        var deliverySha = Git("rev-parse", "HEAD");
        Git("switch", "-q", "develop");
        Write(rule.Path, InsertEntry(original, rule, integrationEntry, atStart));
        Commit("AGT-1: add integration entry");
        var integrationSha = Git("rev-parse", "HEAD");

        // Reproduce the original failure before exercising the fallback.
        Assert.Equal(1, RunGit("merge", "--no-ff", "--no-commit", "delivery").Code);
        Git("merge", "--abort");
        var result = Service().MergeBranchIntoIntegration(_repo, "delivery", "develop");

        Assert.Equal(MergeIntoIntegrationOutcome.Merged, result.Outcome);
        Assert.True(result.ConflictsResolved, result.Error);
        Assert.Empty(result.RebasedCommits);
        Assert.Equal(integrationSha, Git("rev-parse", "HEAD^1"));
        Assert.Equal(deliverySha, Git("rev-parse", "HEAD^2"));
        var merged = File.ReadAllText(Path.Combine(_repo, rule.Path));
        Assert.Equal(InsertEntry(original, rule, integrationEntry + deliveryEntry, atStart), merged);
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData("base-edit")]
    [InlineData("outside-section")]
    [InlineData("unregistered")]
    [InlineData("mixed-files")]
    public void Unsafe_or_unregistered_conflicts_leave_the_integration_tip_and_files_untouched(string kind)
    {
        var (rule, _) = Seed("operator-agent");
        var baseText = kind == "base-edit" ? "existing paragraph" : "";
        var body = $"outside\n{rule.StartMarker}\n{baseText}\n{rule.EndMarker}\n";
        Write(rule.Path, body);
        if (kind == "unregistered")
            File.Delete(Path.Combine(_repo, AppendOnlyRegistry.DirectoryPath, "operator-agent.json"));
        if (kind == "mixed-files") Write("unregistered.md", "base\n");
        Commit("base shape");
        string Changed(string side) => kind switch
        {
            "base-edit" => body.Replace("existing paragraph", side, StringComparison.Ordinal),
            "outside-section" => side + "\n" + body,
            _ => body.Replace(rule.EndMarker, side + "\n" + rule.EndMarker, StringComparison.Ordinal),
        };
        Git("switch", "-qc", "delivery");
        Write(rule.Path, Changed("delivery"));
        if (kind == "mixed-files") Write("unregistered.md", "delivery\n");
        Commit("AGT-2: delivery");
        Git("switch", "-q", "develop");
        Write(rule.Path, Changed("integration"));
        if (kind == "mixed-files") Write("unregistered.md", "integration\n");
        Commit("AGT-1: integration");
        var before = Git("rev-parse", "HEAD");

        var result = Service().MergeBranchIntoIntegration(_repo, "delivery", "develop");

        Assert.False(result.Outcome.IsSuccessfulIntegration());
        Assert.Equal(before, Git("rev-parse", "HEAD"));
        Assert.Equal(Changed("integration"), File.ReadAllText(Path.Combine(_repo, rule.Path)));
        Assert.Equal(string.Empty, Git("status", "--porcelain"));
    }

    [Theory]
    [InlineData("drive-to-conclusion")]
    [InlineData("security-overview")]
    [InlineData("operator-agent")]
    public void Registration_has_unique_ordered_markers(string feature)
    {
        var root = CSharpSourceScanner.RepoRoot();
        var rule = JsonSerializer.Deserialize<AppendOnlyRegistry>(File.ReadAllText(
            Path.Combine(root, AppendOnlyRegistry.DirectoryPath, feature + ".json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var text = File.ReadAllText(Path.Combine(root, rule.Path));
        Assert.Equal(1, text.Split(rule.StartMarker).Length - 1);
        Assert.Equal(1, text.Split(rule.EndMarker).Length - 1);
        Assert.True(text.IndexOf(rule.StartMarker, StringComparison.Ordinal)
            < text.IndexOf(rule.EndMarker, StringComparison.Ordinal));
    }

    private (AppendOnlyRegistry Rule, string Original) Seed(string feature)
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.name", "Registry fixture");
        Git("config", "user.email", "registry@example.invalid");
        Git("config", "core.autocrlf", "false");
        var root = CSharpSourceScanner.RepoRoot();
        var registration = AppendOnlyRegistry.DirectoryPath + "/" + feature + ".json";
        Write(registration, File.ReadAllText(Path.Combine(root, registration)));
        var rule = AppendOnlyRegistry.Find(_repo, JsonSerializer.Deserialize<AppendOnlyRegistry>(
            File.ReadAllText(Path.Combine(root, registration)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Path)!;
        var original = File.ReadAllText(Path.Combine(root, rule.Path));
        Write(rule.Path, original);
        Commit("base");
        return (rule, original);
    }

    private static string Entry(string feature, string side) => feature == "drive-to-conclusion"
        ? $"<tr id=\"fixture-{side}\"><td>{side}</td></tr>\n"
        : $"\n## Fixture {side}\n\n{side} paragraph.\n\n";

    private static string InsertEntry(string original, AppendOnlyRegistry rule, string entry, bool atStart)
    {
        var marker = original.IndexOf(atStart ? rule.StartMarker : rule.EndMarker, StringComparison.Ordinal);
        var offset = atStart ? original.IndexOf('\n', marker) + 1 : original.LastIndexOf('\n', marker) + 1;
        return original.Insert(offset, entry);
    }

    private GitService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void Commit(string message) { Git("add", "."); Git("commit", "-qm", message); }

    private (int Code, string Output, string Error) RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "Fixture Git timed out.");
        return (process.ExitCode, output.GetAwaiter().GetResult().TrimEnd('\r', '\n'), error.GetAwaiter().GetResult());
    }

    private string Git(params string[] args)
    {
        var result = RunGit(args);
        Assert.True(result.Code == 0, result.Error);
        return result.Output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }
}
