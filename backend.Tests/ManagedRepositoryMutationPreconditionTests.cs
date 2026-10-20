using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The compare-and-write precondition of <see cref="ManagedRepositoryMutationService"/>
/// (AGT-3221 review): it runs inside the repository gate, so no other managed
/// write lands between the caller's digest check and its own write, and a
/// refusal writes nothing.
/// </summary>
public sealed class ManagedRepositoryMutationPreconditionTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "managed-mutation-" + Guid.NewGuid().ToString("N"));
    private readonly ManagedRepositoryMutationService _mutations;

    public ManagedRepositoryMutationPreconditionTests()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        var summaries = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summaries);
        _mutations = new ManagedRepositoryMutationService(new GitService(NullLogger<GitService>.Instance, scanner, configuration));
    }

    public void Dispose() => TestTempRoot.TryDelete(_root);

    [Fact]
    public void A_refused_precondition_writes_nothing_and_says_so()
    {
        var wrote = false;

        var result = _mutations.Execute("Website", _root, "test", "test", ["project.yml"],
            () => wrote = true, precondition: () => "stale");

        Assert.False(wrote);
        Assert.Equal((false, true, "stale"), (result.Success, result.PreconditionFailed, result.Error));
    }

    [Fact]
    public void The_precondition_holds_the_repository_gate_until_its_write_is_done()
    {
        var order = new List<string>();
        using var checking = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => _mutations.Execute("Website", _root, "first", "first", ["project.yml"],
            () => { lock (order) order.Add("first-write"); },
            precondition: () =>
            {
                checking.Set();
                release.Wait(Bound);
                lock (order) order.Add("first-check");
                return null;
            }));
        Assert.True(checking.Wait(Bound));

        var second = Task.Run(() => _mutations.Execute("Website", _root, "second", "second", ["project.yml"],
            () => { lock (order) order.Add("second-write"); }));
        var secondFinishedEarly = second.Wait(TimeSpan.FromMilliseconds(200));
        release.Set();
        Task.WaitAll([first, second], Bound);

        Assert.False(secondFinishedEarly);
        Assert.Equal(["first-check", "first-write", "second-write"], order);
        Assert.True(first.Result.Success && second.Result.Success);
    }
}
