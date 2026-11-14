using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// The selection call a local coding launch asks for one card. The launch and
/// the capacity forecast (<c>GET /api/operator/capacity</c>) build it here so
/// the forecast asks exactly the question the next pickup will ask.
/// </summary>
internal static class CodingSelectionCalls
{
    internal const string ExecutionPath = "local-coding-launch";

    internal static ModelSelectionCall For(TaskInfo info, string project, int occupiedSlots,
        DateTime nowUtc, Func<ModelRoutingTier?>? correctnessFloor, bool preview = false)
    {
        var pending = info.PendingIntent?.ModelFallback;
        var (cli, model, thinking) = ProjectRunner.RequestedRoute(
            info.CliType, info.Model, info.ThinkingLevel, pending);
        var preference = info.PinMode != "hard" && info.PreferenceExpiresAt > nowUtc;
        return new ModelSelectionCall(
            new SelectionRequest
            {
                Consumer = SelectionConsumers.Coding,
                Project = project,
                TaskKey = CardKey(info),
                Pins = new SelectionPins(
                    cli,
                    pending is not null ? model : preference ? info.PreferenceModel ?? info.Model : info.Model,
                    pending is not null ? thinking : preference
                        ? info.PreferenceThinkingLevel ?? info.ThinkingLevel : info.ThinkingLevel,
                    info.PinMode == "hard" ? SelectionPinMode.Hard : SelectionPinMode.Preference),
                IsExplicitPin = info.ModelExplicit,
                TaskType = info.TaskType,
            },
            ExecutionPath,
            occupiedSlots,
            CorrectnessFloor: correctnessFloor,
            Preview: preview);
    }

    internal static string CardKey(TaskInfo info)
        => info.Key ?? (string.IsNullOrEmpty(info.TaskKey) ? info.Id : info.TaskKey);
}
