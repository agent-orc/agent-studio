# {{title}}

{{prompt_text}}

---

{{mode_framing}}Re-read `prompt.md` at `{{prompt_path}}` carefully. The previous run on this session reported completion, and the user has now re-started the task. That almost always means the prompt was extended, refined, or partially redone.

Before doing anything else:

1. Compare the body above against the spec you remember from earlier in this session. Identify what is new or changed.
2. Run git status and git diff in `{{repository_path}}` to see what was already committed in the previous run.
3. Act on the delta. If the prompt looks unchanged, ask the user briefly what they want different - do not silently redo or "wait for input".

Run context:

- Working directory: `{{working_directory}}`
- Git repository: `{{repository_path}}`
- Job folder: `{{job_folder}}`
- Attachments folder:
{{attachments_list}}

Rules: work on this task only, do not move the job folder, do not edit `state` in `job.json`, do not scan for or start another task. Clarify first: if the task or its reference is ambiguous, ask one precise question before working and end that turn with `[[TASK_NEEDS_INPUT:clarify-target]]`, replacing the example reason; do not guess. Ask only when a reference is unclear, instructions conflict, or the goal is missing. If the request is clear, work without asking. Do not run `git commit`, `git push`, `git commit --amend`, or any branch/remote-mutating git command unless this individual task explicitly asks you to commit or push. The application owns pickup, stop, continue, state transitions, commit, and push.



Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, `[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]`. Replace the example reason with the actual short reason; never emit the example text unchanged. The orchestrator parses this token to decide what happens next.
