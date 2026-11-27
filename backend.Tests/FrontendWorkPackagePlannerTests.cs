using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class FrontendWorkPackagePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frontend-work-package-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("node scripts/run-unit-tests.mjs")]
    [InlineData("ng test frontend --watch=false --progress=false")]
    public void Focused_slice_uses_the_CI_reporter_instead_of_raw_ng_test(string ciScript)
    {
        var frontend = Path.Combine(_root, "frontend");
        var folder = Path.Combine(frontend, "src", "app", "elevation-control");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(frontend, "package.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            scripts = new Dictionary<string, string> { ["test"] = "ng test", ["test:ci"] = ciScript },
        }));
        File.WriteAllText(Path.Combine(folder, "elevation-control.spec.ts"), "// elevation regression spec");

        var command = FrontendWorkPackagePlanner.Plan(_root,
            ["frontend/src/app/elevation-control/elevation-control.spec.ts"]);

        Assert.NotNull(command);
        Assert.Equal("frontend", command.WorkingSubdir);
        Assert.Equal("npm run test:ci -- --include=\"src/app/elevation-control/*.spec.ts\"", command.Command);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
