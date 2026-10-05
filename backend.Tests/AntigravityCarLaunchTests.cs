using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2373: local Antigravity card runs go through CAR like Claude and Codex.
/// Studio persists Antigravity under the compatibility CLI type <c>gemini</c>,
/// while CAR 0.7.0 registers <c>agentapi</c> as its <c>antigravity</c>
/// descriptor and keeps <c>gemini</c> for the deprecated Gemini CLI. These tests
/// pin that the host adapter selects the Antigravity descriptor, so the
/// configured <c>agentapi</c> binary receives the conversation argv.
/// </summary>
public sealed class AntigravityCarLaunchTests : IDisposable
{
    private const string AgentApiPath = "/opt/antigravity/bin/agentapi";
    private const string ConversationId = "1936e314-4af2-4efb-b588-e1355a32ad16";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "antigravity-car-launch", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Persisted_gemini_type_selects_the_car_antigravity_descriptor()
    {
        Assert.Equal(CliTypes.Antigravity, GenericCliExecutionService.CarCliTypeFor(CliTypes.Gemini));
        Assert.Equal(CliTypes.Antigravity, GenericCliExecutionService.CarCliTypeFor("GEMINI"));
        Assert.Equal(CliTypes.Claude, GenericCliExecutionService.CarCliTypeFor(CliTypes.Claude));
        Assert.Equal(CliTypes.Codex, GenericCliExecutionService.CarCliTypeFor(CliTypes.Codex));
    }

    [Fact]
    public async Task New_conversation_launches_the_configured_agentapi_through_car()
    {
        var launch = await CaptureLaunchAsync(
            prompt: "Implement the card.",
            model: "gemini-3-pro",
            sessionName: null,
            resumeSession: false);

        Assert.Equal(AgentApiPath, launch.Executable);
        Assert.Equal(["new-conversation", "--model=pro", "Implement the card."], launch.Argv);
    }

    [Theory]
    [InlineData("gemini-3-flash-lite", "--model=flash_lite")]
    [InlineData("gemini-3-flash", "--model=flash")]
    [InlineData("gemini-3-pro", "--model=pro")]
    public async Task Car_maps_the_model_to_the_agentapi_tier(string model, string expectedFlag)
    {
        var launch = await CaptureLaunchAsync("Go.", model, sessionName: null, resumeSession: false);

        Assert.Equal("new-conversation", launch.Argv[0]);
        Assert.Equal(expectedFlag, launch.Argv[1]);
    }

    [Fact]
    public async Task Resume_sends_the_message_to_the_captured_conversation_through_car()
    {
        var launch = await CaptureLaunchAsync(
            prompt: "Continue with the review findings.",
            model: "gemini-3-pro",
            sessionName: ConversationId,
            resumeSession: true);

        Assert.Equal(AgentApiPath, launch.Executable);
        Assert.Equal(["send-message", ConversationId, "Continue with the review findings."], launch.Argv);
    }

    [Fact]
    public async Task Car_launch_carries_no_gemini_cli_flags()
    {
        var launch = await CaptureLaunchAsync("Go.", "gemini-3-pro", sessionName: null, resumeSession: false);

        Assert.DoesNotContain("-o", launch.Argv);
        Assert.DoesNotContain("stream-json", launch.Argv);
        Assert.DoesNotContain("-p", launch.Argv);
        Assert.DoesNotContain(launch.Argv, arg => arg.StartsWith("--skip-trust", StringComparison.Ordinal)
                                                  || arg.StartsWith("--yolo", StringComparison.Ordinal));
    }

    private async Task<RecordedLaunch> CaptureLaunchAsync(
        string prompt,
        string model,
        string? sessionName,
        bool resumeSession)
    {
        var workingDirectory = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(workingDirectory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        var service = GenericCliExecutionService.ForAntigravity(
            NullLogger<GenericCliExecutionService>.Instance, configuration);
        service.SetCliPath(AgentApiPath);
        var spawner = new RecordingSpawner();
        service.CarOptionsCustomizer = options => options with { Spawner = spawner };

        var jobKey = $"antigravity-car-{Guid.NewGuid():N}";
        var (execution, error) = await service.StartAsync(
            jobId: jobKey,
            jobKey: jobKey,
            prompt: prompt,
            workingDirectory: workingDirectory,
            sessionName: sessionName,
            resumeSession: resumeSession,
            model: model,
            thinkingLevel: null,
            jobFolderPath: Path.Combine(_root, "task"),
            permissionMode: CliPermissionModes.Yolo,
            contextMode: CliContextModes.Shared);

        // The recording spawner refuses the launch after capturing it, so the
        // host reports a start failure instead of running a real agentapi.
        Assert.Null(execution);
        Assert.NotNull(error);
        return Assert.IsType<RecordedLaunch>(spawner.Launch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed record RecordedLaunch(string Executable, IReadOnlyList<string> Argv);

    private sealed class RecordingSpawner : ICliProcessSpawner
    {
        public RecordedLaunch? Launch { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var executable = startInfo.FileName;
            var argv = startInfo.ArgumentList.ToList();
            // Linux hosts wrap every CLI in `setsid --wait <cli> ...` so the
            // run owns its process group; record the CLI launch underneath.
            if (Path.GetFileName(executable) == "setsid" && argv.Count >= 2 && argv[0] == "--wait")
            {
                executable = argv[1];
                argv = argv.Skip(2).ToList();
            }
            Launch = new RecordedLaunch(executable, argv);
            throw new InvalidOperationException("launch recorded; no agentapi in unit tests");
        }
    }
}
