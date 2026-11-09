using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Machine-local state resolves through <c>LocalStateRoot</c> (AGT-3401).
/// <c>Environment.GetFolderPath(LocalApplicationData)</c> returns an empty
/// string on Linux while the folder is missing, and every direct caller turned
/// that into a path relative to the working directory: in a container, state
/// in the image layer that the next recreate lost.
/// </summary>
[Trait("Category", "Guard")]
public sealed class LocalStateRootGuardTests
{
    private const string Owner = "contracts/TaskServer.Contracts/LocalStateRoot.cs";

    [Fact]
    public void Only_the_local_state_root_reads_the_local_application_data_folder()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var violations = new[] { "backend", "contracts", "task-server" }
            .SelectMany(folder => CSharpSourceScanner.SourceFiles(root, folder))
            .Select(path => (File: CSharpSourceScanner.Relative(root, path),
                Code: CSharpSourceScanner.MaskComments(CSharpSourceScanner.Read(path))))
            .Where(source => source.File != Owner
                && source.Code.Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            .Select(source => source.File)
            .ToList();

        Assert.True(violations.Count == 0,
            "Resolve machine-local state with LocalStateRoot.Resolve() or LocalStateRoot.Combine(...):\n  "
            + string.Join("\n  ", violations));
    }
}
