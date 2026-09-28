# Quota fallback

Workspace Settings > CLI Management shows the current quota windows, each
window's cap and reset, and the fallback route for models known to the workspace.

## When fallback starts

- **Normal:** new work uses its requested provider while every window is below
  its cap.
- **Fallback preferred by operator:** **Prefer fallback now** routes new runs,
  continuations, remote claims, and one-shots to a comparable provider before
  the cap. The switch expires at the next known reset.
- **Fallback active:** a quota window has reached its cap. New work switches
  automatically. Work already running is not interrupted.

Fallback never crosses the task's correctness floor. If every comparable model
is capped, unavailable, retired, or unable to preserve the requested thinking
level, the work waits under the configured reset-wait policy.

## Reading the route table

Each row shows the requested model, its comparable fallback, input/output price
per million tokens for both models, and the source. A Token Economy catalogue
version means the route was derived from the pinned offline catalogue. An
**override** is an operator-configured pair and takes precedence.

The quota probe appears under **Cannot be rerouted** because it runs a
provider-native quota command and does not invoke a model. No credential,
prompt, token, or other secret is shown in this view.

When a run switches, its timeline and card retain the original and effective
model, reason, quota window and usage, and catalogue version. Remote runners use
the task server's recorded decision and do not choose a fallback themselves.
