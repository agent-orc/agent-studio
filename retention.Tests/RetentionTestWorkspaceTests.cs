namespace AgentStudio.Retention.Tests;

public sealed class RetentionTestWorkspaceTests
{
    [Fact]
    public void DisposeDeletesReadOnlyGitObjectFiles()
    {
        var fixture = new RetentionTestWorkspace();
        var root = fixture.Root;
        var objectDirectory = Path.Combine(fixture.Workspace, ".git", "objects", "ab");
        var objectPath = Path.Combine(objectDirectory, "1234567890abcdef");
        Directory.CreateDirectory(objectDirectory);
        File.WriteAllText(objectPath, "temporary object");
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) | FileAttributes.ReadOnly);

        fixture.Dispose();

        Assert.False(Directory.Exists(root));
    }
}
