using System.Diagnostics;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3968: a replay of the AGT-3897 integration rounds of 17 and 18
/// November in a test repository, through the real batch replay (Git merge,
/// union resolution and sibling deferral). Each round merges the card's
/// delivery with the develop tip of that round, the brief's replay rule. The
/// shapes follow the brief: a conflict-free first round (the conflict was
/// with the sibling AGT-3891 in the same batch), three rounds of hunks where
/// both sides only added lines, and the DeployService.cs block where both
/// sides changed the same line. Before AGT-3968 each round was a full coding
/// run; now only the DeployService.cs round needs the agent.
/// </summary>
public sealed class IntegrationRoundReplayTests : IDisposable
{
    private const string DeployArea = "frontend/deploy-area.ts";
    private const string DeployService = "backend/DeployService.cs";
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agt3897-replay-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly BatchGateStore _store;
    private readonly GitService _git;
    private bool _hasCommits;
    private string _filler = string.Empty;

    public IntegrationRoundReplayTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _store = new BatchGateStore(Path.Combine(_root, "batch"));
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        Git("init", "-q", "-b", "develop");
        Git("config", "user.email", "replay@example.invalid");
        Git("config", "user.name", "Replay");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Agt3897Rounds_NeedOneAgentRoundForTheDeployServiceBlock()
    {
        var b0 = Commit("develop", "base", new()
        {
            [DeployArea] = Lines(
                "import { Base } from './base';",
                "export const imports = [",
                "  Base,",
                "];",
                "export function area(): string {",
                "  return 'area';",
                "}"),
            [DeployService] = Lines(
                "public sealed class DeployService",
                "{",
                "    public string Mode() => \"manual\";",
                "}"),
        });
        // AGT-3897: deploy actions; it imports, registers and adds a function,
        // and changes the deploy mode line.
        var delivery = Commit("delivery", "AGT-3897: deploy actions", new()
        {
            [DeployArea] = Lines(
                "import { Base } from './base';",
                "import { Actions } from './actions';",
                "export const imports = [",
                "  Base,",
                "  Actions,",
                "];",
                "export function area(): string {",
                "  return 'area with actions';",
                "}",
                "export function actions(): number { return 1; }"),
            [DeployService] = Lines(
                "public sealed class DeployService",
                "{",
                "    public string Mode() => \"approval\";",
                "}"),
        }, b0);
        Git("update-ref", "refs/heads/agent-studio/results/agt-3897-1", delivery);
        // AGT-3891 in the same batch rewrites the area return line.
        var sibling = Commit("sibling", "AGT-3891: release creation", new()
        {
            [DeployArea] = Lines(
                "import { Base } from './base';",
                "export const imports = [",
                "  Base,",
                "];",
                "export function area(): string {",
                "  return 'area with release';",
                "}"),
        }, b0);
        Git("update-ref", "refs/heads/agent-studio/results/agt-3891-1", sibling);
        // A lone card past its deadline takes the per-task gate, so every
        // one-card round carries an unrelated documentation card behind it.
        _filler = Commit("filler", "DOC-1: note", new() { ["docs/note.md"] = Lines("note") }, b0);
        Git("update-ref", "refs/heads/agent-studio/results/doc-1", _filler);

        var agentRounds = 0;

        // 17 Nov 22:39: develop gained one unrelated commit.
        var r1 = Commit("develop", "Integrate net change from 1aa971f2", new()
        {
            ["backend/Cost.cs"] = Lines("public sealed class Cost { }"),
        });
        var first = Assemble(r1, ("AGT-3891", "agt-3891-1", sibling), ("AGT-3897", "agt-3897-1", delivery));
        Assert.Equal(["AGT-3891"], first.AdmittedKeys);
        Assert.Equal(["AGT-3897"], first.SiblingDeferredKeys);
        Assert.Empty(first.EjectedKeys);
        Assert.Equal(["AGT-3891"], _store.ReadReplay(first.BatchId, "AGT-3897").SiblingKeys);
        agentRounds += first.EjectedKeys.Count;

        // 18 Nov 04:03, 06:04 and 07:29: other cards added lines at the places
        // the delivery added its own (imports, the imports array, a function).
        var r2 = Commit("develop", "Integrate AGT-3907: secrets import", new()
        {
            [DeployArea] = Replace(DeployArea, "import { Base } from './base';\n",
                "import { Base } from './base';\nimport { Secrets } from './secrets';\n"),
        });
        var r3 = Commit("develop", "Integrate AGT-3894: secrets registration", new()
        {
            [DeployArea] = Replace(DeployArea, "  Base,\n", "  Base,\n  Secrets,\n"),
        });
        var r4 = Commit("develop", "Integrate AGT-3895: secrets function", new()
        {
            [DeployArea] = Replace(DeployArea, "}\n", "}\nexport function secrets(): number { return 2; }\n"),
        });
        foreach (var (tip, hunks) in new[] { (r2, 1), (r3, 2), (r4, 3) })
        {
            var round = Assemble(tip, ("AGT-3897", "agt-3897-1", delivery), Filler);
            Assert.Equal(["AGT-3897", "DOC-1"], round.AdmittedKeys);
            agentRounds += round.EjectedKeys.Count;
            var replay = _store.ReadReplay(round.BatchId, "AGT-3897");
            Assert.Equal(hunks, replay.UnionResolvedHunks!.Count);
            Assert.All(replay.UnionResolvedHunks!, hunk => Assert.Equal(DeployArea, hunk.Path));
            var merged = Git("show", $"{round.CandidateSha}:{DeployArea}");
            Assert.DoesNotContain("<<<<<<<", merged);
            Assert.Contains("import { Actions } from './actions';", merged);
            Assert.Contains("import { Secrets } from './secrets';", merged);
            Assert.Contains("  Actions,", merged);
        }

        // 18 Nov 10:47: AGT-3904 changed the same DeployService.cs line. Both
        // sides changed a merge-base line, so the conflict goes to the agent.
        var r5 = Commit("develop", "Integrate AGT-3904: secret binding preflight", new()
        {
            [DeployService] = Replace(DeployService, "\"manual\"", "\"preflight\""),
        });
        var fifth = Assemble(r5, ("AGT-3897", "agt-3897-1", delivery), Filler);
        Assert.Equal(["AGT-3897"], fifth.EjectedKeys);
        var conflict = _store.ReadReplay(fifth.BatchId, "AGT-3897");
        Assert.Contains(DeployService, conflict.ConflictedFiles);
        Assert.Equal(["AGT-3904"], _git.CardsTouchingPaths(_repo, delivery, r5, [DeployService], "AGT-3897"));
        agentRounds += fifth.EjectedKeys.Count;

        // The one agent round merges develop and resolves the block; the next
        // replay admits the delivery.
        var resolved = AgentResolves(delivery, r5);
        Git("update-ref", "refs/heads/agent-studio/results/agt-3897-2", resolved);
        var sixth = Assemble(r5, ("AGT-3897", "agt-3897-2", resolved), Filler);
        Assert.Equal(["AGT-3897", "DOC-1"], sixth.AdmittedKeys);
        agentRounds += sixth.EjectedKeys.Count;

        Assert.Equal(1, agentRounds);
    }

    [Fact]
    public void UnionIsNotUsedWhenAnyHunkChangesAMergeBaseLine()
    {
        var b0 = Commit("develop", "base", new()
        {
            ["a.ts"] = Lines("import { A } from './a';", "export const x = [A];"),
        });
        var delivery = Commit("delivery", "CARD-1: add B", new()
        {
            ["a.ts"] = Lines("import { A } from './a';", "import { B } from './b';", "export const x = [A, B];"),
        }, b0);
        Git("update-ref", "refs/heads/agent-studio/results/card-1", delivery);
        var tip = Commit("develop", "Integrate CARD-2: add C", new()
        {
            ["a.ts"] = Lines("import { A } from './a';", "import { C } from './c';", "export const x = [A, C];"),
        });

        _filler = Commit("filler", "DOC-1: note", new() { ["docs/note.md"] = Lines("note") }, b0);
        Git("update-ref", "refs/heads/agent-studio/results/doc-1", _filler);

        var assembly = Assemble(tip, ("CARD-1", "card-1", delivery), Filler);

        Assert.Equal(["CARD-1"], assembly.EjectedKeys);
        Assert.Equal("conflict", _store.ReadReplay(assembly.BatchId, "CARD-1").Outcome);
        Assert.Equal(tip, Git("rev-parse", "develop").Trim());
    }

    private (string Key, string Ref, string Sha) Filler => ("DOC-1", "doc-1", _filler);

    private BatchGateAssembly Assemble(string baseSha, params (string Key, string Ref, string Sha)[] members)
    {
        var subjects = members.Select((member, index) => new BatchGateSubject(
            member.Key, "project", "repo", "develop", "full", "digest", "v1",
            "refs/heads/agent-studio/results/" + member.Ref, member.Sha, "run-" + member.Ref, 1, 1, member.Sha,
            true, true, true, true, false, null, true, Now.AddMinutes(index), index + 1)).ToArray();
        var manifest = BatchGatePolicy.Form(subjects, Scope, baseSha,
            new BatchGateFormationOptions(Enabled: true, MinimumSize: 1), Now.AddMinutes(30)).Manifest!;
        Assert.Equal(members.Select(member => member.Key), manifest.Members.Select(member => member.TaskKey));
        _store.CloseManifest(manifest);
        return new BatchGateAssembler(_store, _git, _repo, new FakeTimeProvider(Now))
            .Assemble(manifest.BatchId, 1);
    }

    private string AgentResolves(string delivery, string developTip)
    {
        Git("switch", "-q", "--detach", delivery);
        var merge = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "merge", "-q", "--no-edit", developTip }) merge.ArgumentList.Add(argument);
        using (var process = Process.Start(merge)!)
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        File.WriteAllText(Path.Combine(_repo, DeployService), Lines(
            "public sealed class DeployService",
            "{",
            "    public string Mode() => \"approval-after-preflight\";",
            "}"));
        File.WriteAllText(Path.Combine(_repo, DeployArea), Git("show", $"{developTip}:{DeployArea}")
            .Replace("import { Secrets } from './secrets';\n",
                "import { Secrets } from './secrets';\nimport { Actions } from './actions';\n")
            .Replace("  Secrets,\n", "  Secrets,\n  Actions,\n")
            .Replace("  return 'area';\n", "  return 'area with actions';\n")
            + "export function actions(): number { return 1; }\n");
        Git("add", "-A");
        Git("commit", "-qm", "AGT-3897: Merge develop and keep the preflight deploy mode");
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        return sha;
    }

    private string Commit(string branch, string message, Dictionary<string, string> files, string? from = null)
    {
        if (from is not null)
            Git("switch", "-q", "-C", branch, from);
        else if (_hasCommits)
            Git("switch", "-q", branch);
        _hasCommits = true;
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(_repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Git("add", "-A");
        Git("commit", "-qm", message);
        var sha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "-q", "develop");
        return sha;
    }

    private string Replace(string path, string oldValue, string newValue)
    {
        var content = File.ReadAllText(Path.Combine(_repo, path));
        Assert.Contains(oldValue, content);
        var index = content.LastIndexOf(oldValue, StringComparison.Ordinal);
        return content[..index] + newValue + content[(index + oldValue.Length)..];
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    private string Git(params string[] arguments) => Git(arguments, allowFailure: false);

    private string Git(string[] arguments, bool allowFailure)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!allowFailure)
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return process.ExitCode == 0 ? output : string.Empty;
    }
}
