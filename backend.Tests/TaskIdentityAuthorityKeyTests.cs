using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3094: the attempt authority files a card's attempts under the task.json
/// key, falling back to the composite scanner key and then the slug, the same
/// order the claim paths use. Every attempt-authority read goes through
/// <see cref="TaskIdentity.AuthorityKey(string?, string?, string?)"/>.
/// </summary>
public sealed class TaskIdentityAuthorityKeyTests
{
    private const string Composite = "/workspace/projects/demo::remote-review-cannot-clone";

    [Fact]
    public void Card_key_wins_over_the_composite_scanner_key()
    {
        Assert.Equal("AGT-1", TaskIdentity.AuthorityKey("AGT-1", Composite, "remote-review-cannot-clone"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_card_key_falls_back_to_the_task_key_then_the_id(string? key)
    {
        Assert.Equal(Composite, TaskIdentity.AuthorityKey(key, Composite, "slug"));
        Assert.Equal("slug", TaskIdentity.AuthorityKey(key, " ", "slug"));
        Assert.Equal("slug", TaskIdentity.AuthorityKey(key, null, "slug"));
    }

    [Theory]
    [InlineData("AGT-1", Composite, "slug")]
    [InlineData(" ", Composite, "slug")]
    [InlineData(null, "", "slug")]
    public void TaskInfo_and_TaskCoreRecord_overloads_agree(string? key, string taskKey, string id)
    {
        var info = new TaskInfo { Key = key, TaskKey = taskKey, Id = id };
        var core = new TaskCoreRecord { Key = key, TaskKey = taskKey, Id = id };

        Assert.Equal(TaskIdentity.AuthorityKey(key, taskKey, id), TaskIdentity.AuthorityKey(info));
        Assert.Equal(TaskIdentity.AuthorityKey(info), TaskIdentity.AuthorityKey(core));
    }
}
