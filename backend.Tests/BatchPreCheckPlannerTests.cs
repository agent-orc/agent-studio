using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchPreCheckPlannerTests
{
    [Fact]
    public void Selects_build_guards_and_touched_frontend_lint_and_format()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-pre-check-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frontend"));
            File.WriteAllText(Path.Combine(root, "frontend", "package.json"),
                "{\"scripts\":{\"lint\":\"eslint .\",\"format:check\":\"prettier --check .\"}}");
            var plan = new VerifyPlan([
                new(VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "dotnet build app.sln"),
                new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "dotnet test backend.Tests/backend.Tests.csproj --no-build --filter Category!=MachineBound"),
                new(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "npm run test:ci"),
                new(VerifyEcosystem.Custom, VerifyCommandKind.Lint, "", "npm --prefix frontend run lint"),
            ], VerifyPlan.SourceProjectDefinition);

            var backend = BatchPreCheckPlanner.Plan(root, plan, ["backend/routes.cs"], "Auto Review");
            Assert.Equal(2, backend.Commands.Count);
            Assert.Contains("FullyQualifiedName~.Architecture.|Category=Guard", backend.Commands[1].Command);
            Assert.DoesNotContain("Category!=MachineBound --filter", backend.Commands[1].Command);

            var frontend = BatchPreCheckPlanner.Plan(root, plan, ["frontend/src/app/app.ts"], "Auto Review");
            Assert.Contains(frontend.Commands, command => command.Command == "npm --prefix frontend run lint");
            Assert.Contains(frontend.Commands, command => command.Command == "npm --prefix frontend run format:check");
            Assert.DoesNotContain(frontend.Commands, command => command.Command == "npm run test:ci");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Opportunistic_slot_leaves_first_slot_for_regular_batch()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-pre-check-slots-" + Guid.NewGuid().ToString("N"));
        var slots = new MachineGateSlots(root);
        using var preCheck = await slots.AcquireAsync(2, TimeSpan.FromSeconds(1), null,
            CancellationToken.None, reserveFirstSlot: true);
        Assert.NotNull(preCheck);
        Assert.Equal(1, preCheck.Slot);
        using var batch = await slots.AcquireAsync(2, TimeSpan.FromSeconds(1), null, CancellationToken.None);
        Assert.NotNull(batch);
        Assert.Equal(0, batch.Slot);
        using var blocked = await slots.AcquireAsync(2, TimeSpan.FromMilliseconds(150), null,
            CancellationToken.None, reserveFirstSlot: true);
        Assert.Null(blocked);
    }
}
