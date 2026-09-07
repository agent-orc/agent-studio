namespace AgentStudio.Pipeline;

/// <summary>
/// Resolves the model / CLI / reasoning effort the <c>post-task-spawner</c> step
/// (AGT-2028) uses to judge relevance and generate the follow-up task prompt.
/// The spawn evaluation is quality-first by design: it defaults to the catalogue's
/// best available Codex model at its top advertised effort, mirroring the ASS-1657
/// code-review-grade asymmetry (the bounded aspect verdicts use the GPT Mini
/// family while an operator-facing judgment gets the strong model).
///
/// <para>
/// "Best available" is not a hard-coded id: it reads
/// <see cref="ModelMetadataRegistry.DefaultForCli(string?)"/>, which returns the
/// vendor's live/default catalogue entry. When a
/// stronger model is later marked default, the spawner follows it automatically -
/// the operator-direktive "bestes verfuegbares Modell ... kuenftig hoeher".
/// A deployment can still override any dimension via
/// <c>TaskSpawnerStep:DefaultModel</c> / <c>:DefaultCli</c> /
/// <c>:DefaultThinkingLevel</c>, and a project can override the model / CLI via
/// its <c>PipelineSteps</c> entry (resolved by
/// <see cref="PipelineStepConfigResolver"/>).
/// </para>
/// </summary>
public static class TaskSpawnerModelSelector
{
    /// <summary>Default CLI for the spawn evaluation pass.</summary>
    public const string DefaultCli = CliTypes.Codex;

    /// <summary>
    /// Default reasoning effort: the top of the selected Codex model's ladder. The spawn
    /// decision is low-frequency and high-leverage (it authors a whole task), so
    /// it is worth the strongest reasoning.
    /// </summary>
    public static string DefaultThinkingLevel =>
        ModelMetadataRegistry.DefaultThinkingLevelForCli(DefaultCli, DefaultModel) ?? "high";

    /// <summary>
    /// The catalogue's current best available Codex model, tracked live rather
    /// than pinned so a future upgrade is picked up for free. Falls back to the
    /// static gpt-5.5 id if live discovery has not published a newer flagship.
    /// </summary>
    public static string DefaultModel =>
        ModelMetadataRegistry.DefaultForCli(DefaultCli) ?? ModelIds.Gpt55;

    /// <summary>
    /// Resolve the effective (model, cli, thinkingLevel) for the spawn pass,
    /// layering per-deployment config over the quality-first defaults. A blank /
    /// whitespace-only override falls back to the default rather than running on
    /// an empty id.
    /// </summary>
    public static (string Model, string Cli, string ThinkingLevel) Resolve(
        string? configuredModel, string? configuredCli, string? configuredThinkingLevel)
    {
        var model = string.IsNullOrWhiteSpace(configuredModel) ? DefaultModel : configuredModel.Trim();
        var cli = string.IsNullOrWhiteSpace(configuredCli) ? DefaultCli : configuredCli.Trim();
        var thinking = string.IsNullOrWhiteSpace(configuredThinkingLevel)
            ? DefaultThinkingLevel
            : configuredThinkingLevel.Trim();
        return (model, cli, thinking);
    }
}
