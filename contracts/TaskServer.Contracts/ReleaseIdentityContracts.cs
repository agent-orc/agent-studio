namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Immutable deployment identity of one runner daemon process (AGT-2826).
/// <para>
/// <c>RunnerVersion</c> on the registration and runner DTOs already carries the
/// release id as an opaque label. It is not enough to tell an operator whether a
/// host still runs the code the Stable release ships: two hosts can carry the
/// same label from different builds, and a release id alone gives no age. This
/// record adds the two facts the drift comparison needs - the product version
/// and the source commit - plus the build timestamp that turns "older" into a
/// number of hours.
/// </para>
/// <para>
/// Every field except <see cref="ReleaseId"/> and <see cref="Version"/> is
/// optional: a host built outside the release pipeline reports what it knows and
/// the comparison degrades to a version ordering rather than refusing to answer.
/// </para>
/// <para>
/// <see cref="ImageRef"/> is the digest-pinned container image a review role runs
/// in (<c>repository@sha256:...</c>), as handed to the container by the deploy
/// plan. Execution Hosts shows it next to the image the server expects for that
/// host (AGT-3689). Null outside a container.
/// </para>
/// </summary>
public sealed record RunnerReleaseIdentityDto(
    string ReleaseId,
    string Version,
    string? Commit = null,
    DateTime? BuiltAt = null,
    string? ImageRef = null);
