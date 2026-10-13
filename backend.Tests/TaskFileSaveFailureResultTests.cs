using AgentStudio.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Status matrix for a job-file save that failed on disk (AGT-3075): a task
/// folder that moved under the write is a conflict, not a file lock.
/// </summary>
public sealed class TaskFileSaveFailureResultTests
{
    public static TheoryData<IOException, int> Failures => new()
    {
        { new DirectoryNotFoundException("Could not find a part of the path."), StatusCodes.Status409Conflict },
        { new FileNotFoundException("Could not find file."), StatusCodes.Status409Conflict },
        { new IOException("The process cannot access the file because it is being used by another process."), StatusCodes.Status503ServiceUnavailable },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Maps_the_save_failure_to_its_status(IOException failure, int expected)
    {
        var result = TaskFilesEndpoints.SaveFailureResult(failure);

        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public void Recording_a_brief_for_a_moved_task_folder_does_not_recreate_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "brief-moved-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => BriefVersionStore.Record(folder, "Sharpened brief"));
        Assert.False(Directory.Exists(folder));
    }
}
