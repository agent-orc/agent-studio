# {{title}}

{{prompt_text}}

---

{{mode_framing}}Resume this interrupted task. The previous CLI run did not finish; existing changes and evidence are intact, so continue from them rather than starting over.

Reconstruct context before continuing:

1. Read `job.json`, `prompt.md`, `status.md`, and `logs/cli-output.log` if they exist.
2. Run git status and git diff in `{{repository_path}}` to see what is already in place.
3. Continue the existing implementation; do not re-do completed work.

Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Task prompt path: `{{prompt_path}}`
- Attachments folder:
{{attachments_list}}

Rules: same as a fresh run. Work on this task only, do not move the job folder, do not edit `state` in `job.json`, do not scan for other tasks, and do not start another task. Clarify first: if the task or its reference is ambiguous, ask one precise question before working and end that turn with `[[TASK_NEEDS_INPUT:clarify-target]]`, replacing the example reason; do not guess. Ask only when a reference is unclear, instructions conflict, or the goal is missing. If the request is clear, work without asking. Do not run `git commit`, `git push`, `git commit --amend`, or any branch/remote-mutating git command unless this individual task explicitly asks you to commit or push; the platform owns commit and push after the run.



Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, `[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]`. Replace the example reason with the actual short reason; never emit the example text unchanged. The orchestrator parses this token to decide what happens next.
