using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2029 waits-on evaluation: the pure <see cref="WaitsOnEvaluator"/> rules
/// (fulfilled once the target reaches 6-completed/7-archive, blocked while any
/// dependency is open or unknown, cycle detection) exercised through
/// <see cref="TaskReferenceIndex.EvaluateWaitsOn"/> so the index wiring the
/// endpoint overlay and the runner gate both use is covered too. Cross-project
/// resolution is implicit: the index is built from a flat multi-project task
/// set and keys are globally unique.
/// </summary>
public class WaitsOnEvaluatorTests
{
    [Fact]
    public void NoDependencies_IsEmpty_NotBlocked()
    {
        var subject = Task("app", "APP-1", TaskStates.Ready);
        var index = TaskReferenceIndex.Build(new[] { subject });

        var status = index.EvaluateWaitsOn(subject);

        Assert.True(status.IsEmpty);
        Assert.False(status.Blocked);
        Assert.False(status.CycleDetected);
    }

    [Fact]
    public void CompletedTarget_IsFulfilled_NotBlocked()
    {
        var target = Task("lib", "LIB-1", TaskStates.Completed, watchPath: "/ws/lib");
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" });
        var index = TaskReferenceIndex.Build(new[] { subject, target });

        var status = index.EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.Resolved);
        Assert.True(item.Fulfilled);
        Assert.Equal("/ws/lib", item.TargetWatchPath);
        Assert.False(status.Blocked);
    }

    [Fact]
    public void ArchivedTarget_CountsAsFulfilled()
    {
        // Fulfilled = 6-completed OR 7-archive; a dependency that has already
        // been archived must not regress a card back to "waiting".
        var target = Task("lib", "LIB-1", TaskStates.Archive, watchPath: "/ws/lib");
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" });
        var index = TaskReferenceIndex.Build(new[] { subject, target });

        var status = index.EvaluateWaitsOn(subject);

        Assert.True(Assert.Single(status.Items).Fulfilled);
        Assert.False(status.Blocked);
    }

    [Fact]
    public void ReleaseGate_TerminalTargetWithoutFlag_RemainsBlockedForRelease()
    {
        var target = Task("lib", "LIB-1", TaskStates.Completed, released: false);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.ReleaseGate);
        Assert.True(item.WaitingForRelease);
        Assert.False(item.TargetReleased);
        Assert.False(item.Fulfilled);
        Assert.True(status.Blocked);
    }

    [Fact]
    public void ReleaseGate_TerminalReleasedTarget_IsFulfilled()
    {
        var target = Task("lib", "LIB-1", TaskStates.Completed, released: true);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.ReleaseGate);
        Assert.True(item.TargetReleased);
        Assert.False(item.WaitingForRelease);
        Assert.True(item.Fulfilled);
        Assert.False(status.Blocked);
    }

    // AGT-2818: a release gate pointing at an archived, never released target is
    // a gate no run can open. It is a configuration error like a cycle, not a
    // wait, and the distinction has to survive all the way to the card.
    [Fact]
    public void ReleaseGate_ArchivedUnreleasedTarget_IsBlockedAndUnsatisfiable()
    {
        var target = Task("lib", "LIB-1", TaskStates.Archive, released: false);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.False(item.Fulfilled);
        Assert.True(item.Unsatisfiable);
        Assert.Contains("archived", item.UnsatisfiableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIB-1", item.UnsatisfiableReason, StringComparison.Ordinal);
        Assert.True(status.Blocked);
        Assert.True(status.UnsatisfiableGate);
        // The way out is still an explicit release, so the flag the release
        // affordance keys on must stay set.
        Assert.True(item.WaitingForRelease);
    }

    [Fact]
    public void ReleaseGate_ArchivedReleasedTarget_IsFulfilled()
    {
        var target = Task("lib", "LIB-1", TaskStates.Archive, released: true);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.Fulfilled);
        Assert.False(item.Unsatisfiable);
        Assert.False(status.Blocked);
        Assert.False(status.UnsatisfiableGate);
    }

    [Fact]
    public void ReleaseGate_CompletedUnreleasedTarget_WaitsForRelease_ButIsSatisfiable()
    {
        // 6-completed is terminal, but the card can still be released by an
        // operator or a later run, so this stays an honest wait.
        var target = Task("lib", "LIB-1", TaskStates.Completed, released: false);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.WaitingForRelease);
        Assert.False(item.Unsatisfiable);
        Assert.Equal("", item.UnsatisfiableReason);
        Assert.True(status.Blocked);
        Assert.False(status.UnsatisfiableGate);
    }

    [Fact]
    public void ArchivedTargetWithoutReleaseGate_IsFulfilled_NotUnsatisfiable()
    {
        // Only a release-gated edge can be unsatisfiable: an ungated edge is
        // fulfilled the moment its target reaches the archive lane.
        var target = Task("lib", "LIB-1", TaskStates.Archive, released: false);
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" });
        var status = TaskReferenceIndex.Build(new[] { subject, target }).EvaluateWaitsOn(subject);

        Assert.True(Assert.Single(status.Items).Fulfilled);
        Assert.False(status.UnsatisfiableGate);
    }

    [Fact]
    public void UnknownKey_StaysUnresolved_AndIsNotClassifiedUnsatisfiable()
    {
        // A key that does not exist yet may still be created; that is a wait,
        // not a gate that can never open.
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "GHOST-9" }, releaseGate: true);
        var status = TaskReferenceIndex.Build(new[] { subject }).EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.False(item.Resolved);
        Assert.False(item.Unsatisfiable);
        Assert.True(status.Blocked);
        Assert.False(status.UnsatisfiableGate);
    }

    [Fact]
    public void OpenTarget_IsBlocked()
    {
        var target = Task("lib", "LIB-1", TaskStates.Ready, watchPath: "/ws/lib");
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1" });
        var index = TaskReferenceIndex.Build(new[] { subject, target });

        var status = index.EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.True(item.Resolved);
        Assert.False(item.Fulfilled);
        Assert.True(status.Blocked);
    }

    [Fact]
    public void UnknownKey_IsUnresolved_AndBlocks()
    {
        // A not-yet-created target: allowed on write (warning), but it blocks
        // pickup until it exists and completes.
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "GHOST-9" });
        var index = TaskReferenceIndex.Build(new[] { subject });

        var status = index.EvaluateWaitsOn(subject);

        var item = Assert.Single(status.Items);
        Assert.False(item.Resolved);
        Assert.False(item.Fulfilled);
        Assert.Null(item.TargetJobId);
        Assert.True(status.Blocked);
    }

    [Fact]
    public void MixedDependencies_OneOpen_IsBlocked_ItemsReflectEach()
    {
        var done = Task("lib", "LIB-1", TaskStates.Completed, watchPath: "/ws/lib");
        var open = Task("web", "WEB-2", TaskStates.Progress, watchPath: "/ws/web");
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1", "WEB-2" });
        var index = TaskReferenceIndex.Build(new[] { subject, done, open });

        var status = index.EvaluateWaitsOn(subject);

        Assert.Equal(2, status.Items.Count);
        Assert.True(status.Items.Single(i => i.Key == "LIB-1").Fulfilled);
        Assert.False(status.Items.Single(i => i.Key == "WEB-2").Fulfilled);
        Assert.True(status.Blocked);
    }

    [Fact]
    public void DuplicateAndSelfEdges_AreIgnored()
    {
        var target = Task("lib", "LIB-1", TaskStates.Ready, watchPath: "/ws/lib");
        var subject = Task("app", "APP-1", TaskStates.Ready, deps: new[] { "LIB-1", "LIB-1", "APP-1" });
        var index = TaskReferenceIndex.Build(new[] { subject, target });

        var status = index.EvaluateWaitsOn(subject);

        // LIB-1 once (dedup), APP-1 (self) dropped.
        Assert.Equal(new[] { "LIB-1" }, status.Items.Select(i => i.Key).ToArray());
    }

    [Fact]
    public void DirectCycle_IsDetected()
    {
        var a = Task("a", "APP-1", TaskStates.Ready, deps: new[] { "APP-2" });
        var b = Task("b", "APP-2", TaskStates.Ready, deps: new[] { "APP-1" });
        var index = TaskReferenceIndex.Build(new[] { a, b });

        Assert.True(index.EvaluateWaitsOn(a).CycleDetected);
        Assert.True(index.EvaluateWaitsOn(b).CycleDetected);
    }

    [Fact]
    public void IndirectCycle_IsDetected()
    {
        var a = Task("a", "APP-1", TaskStates.Ready, deps: new[] { "APP-2" });
        var b = Task("b", "APP-2", TaskStates.Ready, deps: new[] { "APP-3" });
        var c = Task("c", "APP-3", TaskStates.Ready, deps: new[] { "APP-1" });
        var index = TaskReferenceIndex.Build(new[] { a, b, c });

        Assert.True(index.EvaluateWaitsOn(a).CycleDetected);
    }

    [Fact]
    public void Dag_IsNotFlaggedAsCycle()
    {
        // A->B, A->C, B->D, C->D — no cycle.
        var a = Task("a", "APP-1", TaskStates.Ready, deps: new[] { "APP-2", "APP-3" });
        var b = Task("b", "APP-2", TaskStates.Ready, deps: new[] { "APP-4" });
        var c = Task("c", "APP-3", TaskStates.Ready, deps: new[] { "APP-4" });
        var d = Task("d", "APP-4", TaskStates.Completed);
        var index = TaskReferenceIndex.Build(new[] { a, b, c, d });

        Assert.False(index.EvaluateWaitsOn(a).CycleDetected);
    }

    private static TaskInfo Task(
        string id,
        string? key,
        string state,
        string[]? deps = null,
        string watchPath = "/ws/demo",
        bool releaseGate = false,
        bool released = false) => new()
    {
        Id = id,
        Key = key,
        Title = id.ToUpperInvariant(),
        State = state,
        Released = released,
        WatchPath = watchPath,
        References = new TaskReferences
        {
            DependsOn = (deps ?? Array.Empty<string>())
                .Select(key => new TaskDependencyReference(key, releaseGate))
                .ToList(),
        },
    };
}
