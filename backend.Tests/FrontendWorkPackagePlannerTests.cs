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
        Assert.Equal("npm run test:ci -- --include=\"src/app/elevation-control/elevation-control.spec.ts\"", command.Command);
    }

    [Theory]
    [InlineData("view.html")]
    [InlineData("skin.scss")]
    [InlineData("legacy.component.css")]
    public void Resource_only_changes_resolve_the_declared_owner_and_its_consumers(string resource)
    {
        Package();
        Write("src/app/child/renamed.component.ts", """
            @Component({ templateUrl: './view.html', styleUrl: './skin.scss', styleUrls: ['./legacy.component.css'] })
            export class Child {}
            """);
        Spec("src/app/child/renamed.component.spec.ts");
        Write("src/app/parent/parent.ts", "import { Child } from '../child/renamed.component'; export class Parent {}");
        Spec("src/app/parent/parent.spec.ts");
        var command = FrontendWorkPackagePlanner.Plan(_root, ["frontend/src/app/child/" + resource]);
        Assert.Equal(["src/app/child/renamed.component.spec.ts", "src/app/parent/parent.spec.ts"], Includes(command));
    }

    [Theory]
    [InlineData("export { Child as PublicChild } from './child';")]
    [InlineData("import { Child as Local } from './child'; export { Local as PublicChild };")]
    [InlineData("export * from './renaming';")]
    public void Named_imports_resolve_aliases_barrels_and_symbol_ownership(string barrel)
    {
        Package();
        Write("tsconfig.json", """
            { // path aliases use the package root
              "compilerOptions": { "paths": { "@child": ["./src/app/child/index.ts"], "@child/*": ["./src/app/child/*"] } }
            }
            """);
        Write("src/app/child/child.ts", "export class Child {} export interface Options {}");
        Write("src/app/child/index.ts", barrel);
        Write("src/app/child/renaming.ts", "export { Child as PublicChild } from './child'; export * from './index';");
        Spec("src/app/child/child.spec.ts");
        Write("src/app/direct.spec.ts", "import { Child as Subject } from '@child/child'; it('runs', () => new Subject());");
        Write("src/app/reference.spec.ts", "it('uses a global child', () => new Child());");
        Write("src/app/parent/parent.ts", "import { PublicChild as Rendered } from '@child'; export class Parent {}");
        Spec("src/app/parent/parent.spec.ts");
        Write("src/app/other/child.ts", "export class Child {}");
        Write("src/app/other/other.ts", "import { Child } from './child'; export class Other {}");
        Spec("src/app/other/other.spec.ts");
        Write("src/app/wrong.spec.ts", "import { Child } from './other/child'; it('runs', () => new Child());");
        Write("src/app/comment.spec.ts", "// import { Child } from './child/child';\nconst note = 'Child';");
        Write("src/app/grand/grand.ts", "import { Parent } from '../parent/parent'; export class Grand {}");
        Spec("src/app/grand/grand.spec.ts");
        var command = FrontendWorkPackagePlanner.Plan(_root, ["frontend/src/app/child/child.ts"]);
        Assert.Equal(["src/app/child/child.spec.ts", "src/app/direct.spec.ts", "src/app/parent/parent.spec.ts", "src/app/reference.spec.ts"], Includes(command));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void Feature_expansion_caps_the_union_of_all_touched_features(int count, bool expanded)
    {
        Package();
        Write("src/app/features/a/child.ts", "export class Child {}");
        Spec("src/app/features/a/child.spec.ts");
        Write("src/app/parent/parent.ts", "import { Child } from '../features/a/child'; export class Parent {}");
        Spec("src/app/parent/parent.spec.ts");
        Spec("src/app/features/b/changed.spec.ts");
        for (var index = 2; index < count; index++) Spec($"src/app/features/b/peer{index}.spec.ts");
        var command = FrontendWorkPackagePlanner.Plan(_root,
            ["frontend/src/app/features/a/child.ts", "frontend/src/app/features/b/changed.spec.ts"]);
        var includes = Includes(command);
        Assert.Equal((expanded ? count : 2) + 1, includes.Length);
        Assert.Contains("src/app/features/a/child.spec.ts", includes);
        Assert.Contains("src/app/features/b/changed.spec.ts", includes);
        Assert.Contains("src/app/parent/parent.spec.ts", includes);
        Assert.Contains(expanded ? "touched-feature union 8 specs" : "exceeds eight", command!.SelectionReason);
    }

    [Fact]
    public void Collision_floor_is_deduplicated_and_playwright_is_never_discovered()
    {
        Package();
        foreach (var path in new[] { "src/app/app.spec.ts", "src/app/features/studio-shell/studio-shell.component.spec.ts", "src/app/features/task-detail/task-detail.spec.ts" }) Spec(path);
        Spec("e2e/browser.spec.ts");
        Spec("src/e2e/browser.spec.ts");
        Spec("src/app/browser.e2e.spec.ts");
        var command = FrontendWorkPackagePlanner.Plan(_root,
            ["frontend/src/app/app.spec.ts", "frontend/e2e/browser.spec.ts", "frontend/src/e2e/browser.spec.ts", "frontend/src/app/browser.e2e.spec.ts"]);
        Assert.Equal(3, Includes(command).Length);
        Assert.Equal(Includes(command).Distinct(), Includes(command));
        Assert.DoesNotContain("browser", command!.Command);
    }

    [Theory]
    [InlineData("docs/frontend.md")]
    [InlineData("frontend/README.md")]
    public void Documentation_has_no_Angular_invocation(string path)
    {
        Package();
        Spec("src/app/app.spec.ts");
        Assert.Null(FrontendWorkPackagePlanner.Plan(_root, [path]));
    }

    [Fact]
    public void Missing_companions_are_evidence_gaps_and_selection_is_not_execution()
    {
        Package();
        Write("src/app/child.ts", "export class Child {}");
        Spec("src/app/app.spec.ts");
        var command = FrontendWorkPackagePlanner.Plan(_root, ["frontend/src/app/child.ts"]);
        Assert.Contains("missing matches: frontend/src/app/child.spec.ts", command!.SelectionReason);
        Assert.Contains("execution and zero-test evidence come from run-unit-tests", command.SelectionReason);
        Assert.StartsWith("npm run test:ci --", command.Command);
    }

    private void Package() => Write("package.json", """{"scripts":{"test:ci":"node scripts/run-unit-tests.mjs"}}""");

    private void Spec(string path) => Write(path, "it('runs', () => {});");

    private void Write(string path, string content)
    {
        var absolute = Path.Combine(_root, "frontend", path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    private static string[] Includes(VerifyCommand? command)
    {
        Assert.NotNull(command);
        return System.Text.RegularExpressions.Regex.Matches(command.Command, @"--include=""([^""]+)""")
            .Select(match => match.Groups[1].Value).ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
