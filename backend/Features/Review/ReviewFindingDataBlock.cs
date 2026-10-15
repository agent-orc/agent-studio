using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>
/// AGT-2989 - the data boundary between a reviewer and the implementer.
///
/// <para>
/// Reviewer finding text is model output about an untrusted diff, so it can
/// carry anything the diff carried, including text that reads like an
/// instruction. Follow-up prompts therefore never splice it into their own
/// directions. It is quoted between two fence lines tagged with a fresh random
/// nonce, under an explicit frame that the fenced lines are data and not
/// instructions. A finding cannot close the fence early because it cannot know
/// the nonce, and a data line that happens to contain it forces a new nonce.
/// </para>
/// </summary>
public static class ReviewFindingDataBlock
{
    // AGT-3061: the fence and its frame live in the shared contract so the
    // standalone Task Server renders the same finding-round prompt.
    public static string NewNonce() => ReviewFindingRoundPrompt.NewNonce();

    public static string BeginFence(string nonce) => ReviewFindingRoundPrompt.BeginFence(nonce);

    public static string EndFence(string nonce) => ReviewFindingRoundPrompt.EndFence(nonce);

    /// <summary>
    /// Frames <paramref name="dataLines"/> as quoted reviewer data. A supplied
    /// <paramref name="nonce"/> is used unless the data contains it; tests pass
    /// one to get a deterministic prompt.
    /// </summary>
    public static string Render(IReadOnlyList<string> dataLines, string? nonce = null)
        => ReviewFindingRoundPrompt.RenderData(dataLines, nonce);

    /// <summary>Per-aspect Remote Review findings as one fenced data block.</summary>
    public static string RenderAspectFindings(IReadOnlyList<ReviewFollowUpFinding> findings, string? nonce = null)
        => ReviewFindingRoundPrompt.RenderAspectFindings(findings, nonce);
}
