using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3011 review finding: a fix round must never leave a Ready card without
/// its fix instructions. The material is prepared in the Human Review folder
/// before the lane move, and a refused or failed move restores the folder so
/// the next sweep tick can retry the card.
/// </summary>
public sealed class OperatorSweepFixRoundTransactionTests : IDisposable
{
    private const string OriginalPrompt = "# Task\n\nShip the sweeps.\n";
    private readonly string _root;
    private readonly string _reviewFolder;
    private readonly string _readyFolder;

    public OperatorSweepFixRoundTransactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "operator-sweep-fix-round-" + Guid.NewGuid().ToString("N"));
        _reviewFolder = Path.Combine(_root, TaskStates.HumanReview, "card");
        _readyFolder = Path.Combine(_root, TaskStates.Ready, "card");
        Directory.CreateDirectory(_reviewFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(_readyFolder)!);
        File.WriteAllText(Path.Combine(_reviewFolder, "prompt.md"), OriginalPrompt);
        File.WriteAllText(Path.Combine(_reviewFolder, "task.json"),
            JsonSerializer.Serialize(new { id = "card", state = TaskStates.HumanReview, tags = new[] { "frontend" } }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task RefusedMove_RestoresPromptFollowUpAndTags_SoTheCardStaysRetryable()
    {
        var result = await RunAsync(_ => Task.FromResult(new MoveJobOutcome(MoveJobStatus.SourceStateMismatch, "lane changed")));

        Assert.False(result.Started);
        Assert.Contains("refused", result.Detail);
        AssertFolderRestored();
    }

    [Fact]
    public async Task MoveThatThrows_BeforeLeavingTheLane_RestoresTheFolder_AndSurfacesTheFailure()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            RunAsync(_ => throw new IOException("disk full"), stillInSourceLane: () => true));

        AssertFolderRestored();
    }

    [Fact]
    public async Task FailedPromptNote_NeverMovesTheCard()
    {
        var moves = 0;
        var result = await RunAsync(
            _ =>
            {
                moves++;
                return Task.FromResult(new MoveJobOutcome(MoveJobStatus.Success, null, _readyFolder));
            },
            appendNote: () => false);

        Assert.False(result.Started);
        Assert.Equal(0, moves);
        AssertFolderRestored();
    }

    [Fact]
    public async Task SuccessfulMove_CarriesEveryInstructionIntoReady()
    {
        var result = await RunAsync(_ =>
        {
            // The guarded move sees the prepared folder and carries it along.
            Assert.True(File.Exists(Path.Combine(_reviewFolder, "orchestrator-follow-up.md")));
            Directory.Move(_reviewFolder, _readyFolder);
            return Task.FromResult(new MoveJobOutcome(MoveJobStatus.Success, null, _readyFolder));
        });

        Assert.True(result.Started);
        Assert.Contains("Fix the failing gate.", File.ReadAllText(Path.Combine(_readyFolder, "prompt.md")));
        Assert.Contains("Fix the failing gate.", File.ReadAllText(Path.Combine(_readyFolder, "orchestrator-follow-up.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(_readyFolder, "orchestrator-follow-up-history")));
        Assert.Contains(ReviewDecisionOrchestrator.ReissueTagId,
            OperatorSweepActions.FixRoundTransaction.ReadTags(_readyFolder)!);
    }

    private Task<OperatorSweepActionResult> RunAsync(
        Func<CancellationToken, Task<MoveJobOutcome>> move,
        Func<bool>? appendNote = null,
        Func<bool>? stillInSourceLane = null)
    {
        var promptPath = Path.Combine(_reviewFolder, "prompt.md");
        return OperatorSweepActions.FixRoundTransaction.RunAsync(
            _reviewFolder,
            "card",
            new OperatorSweepFixRound("review_1", "Fix the failing gate."),
            appendNote ?? (() =>
            {
                File.AppendAllText(promptPath, "\n## Continuous Session Note\n\nFix the failing gate.\n");
                return true;
            }),
            previous =>
            {
                if (previous is null) File.Delete(promptPath);
                else File.WriteAllText(promptPath, previous);
                return true;
            },
            move,
            stillInSourceLane ?? (() => Directory.Exists(_reviewFolder)),
            NullLogger.Instance,
            CancellationToken.None);
    }

    private void AssertFolderRestored()
    {
        Assert.True(Directory.Exists(_reviewFolder));
        Assert.Equal(OriginalPrompt, File.ReadAllText(Path.Combine(_reviewFolder, "prompt.md")));
        Assert.False(File.Exists(Path.Combine(_reviewFolder, "orchestrator-follow-up.md")));
        var history = Path.Combine(_reviewFolder, "orchestrator-follow-up-history");
        Assert.True(!Directory.Exists(history) || Directory.GetFiles(history).Length == 0);
        Assert.Equal(["frontend"], OperatorSweepActions.FixRoundTransaction.ReadTags(_reviewFolder));
    }
}
