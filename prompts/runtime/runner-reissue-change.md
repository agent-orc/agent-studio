# {{title}}

## Reissue change prompt

The previous run delivered work, but auto-review sent this task back. The review findings below are the primary task for this run. Fix them directly before doing any other work. Do not restart from the original prompt as if no review happened.

If a finding cannot be resolved, stop and end with `[[TASK_BLOCKED:missing-dependency-xyz]]`, replacing the example reason with the actual short reason. If the findings are resolved and the task is genuinely complete, end with `[[TASK_DONE]]`.

Clarify first: if the task, a finding, or its reference is ambiguous, ask one precise question before working and end that turn with `[[TASK_NEEDS_INPUT:clarify-target]]`, replacing the example reason; do not guess. Ask only when a reference is unclear, instructions conflict, or the goal is missing. If the request is clear, work without asking.

## Review findings to fix now

{{reissue_findings}}

## Full reissue context

{{reissue_followup}}

## Original task context

{{prompt_text}}

---

{{mode_framing}}Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Canonical task prompt: `{{prompt_path}}`
- Attachments folder (relative `attachments/<file>` paths resolve under the job folder):
{{attachments_list}}

Before editing, run git status and git diff in `{{repository_path}}`. Read `orchestrator-follow-up.md`, `code-review-*.md`, and `aspect-*.md` from the job folder when present; they are the review evidence behind the findings above.

Rules: work on this task only, do not move the job folder, do not edit `state` in `job.json`, do not scan for or start another task. Please do not commit or push yourself; the platform commits after review. If you did commit, that is not a problem: it will be shown and cleaned up where safe. Never push to a protected branch or rewrite history that existed before this run. The application owns pickup, stop, continue, and state transitions.



Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, `[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]`. Replace the example reason with the actual short reason; never emit the example text unchanged. The orchestrator parses this token to decide what happens next.
