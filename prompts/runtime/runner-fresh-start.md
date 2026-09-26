# {{title}}

{{prompt_text}}

---

{{mode_framing}}Context for this run (read after the task above):

- Working directory: `{{working_directory}}`
- Git repository for status/diff/commits: `{{repository_path}}`
- Job folder for task metadata and evidence: `{{job_folder}}`
- Task prompt path on disk: `{{prompt_path}}`
- Attachments folder (relative `attachments/<file>` paths resolve under the job folder):
{{attachments_list}}

Rules for this run:

- Work on this task only. Do not scan for or pick up other tasks.
- Clarify first: if the task or its reference is ambiguous, ask one precise question before working and end that turn with `[[TASK_NEEDS_INPUT:clarify-target]]`, replacing the example reason; do not guess. Ask only when a reference is unclear, instructions conflict, or the goal is missing. If the request is clear, work without asking.
- Do not move the job folder. Do not edit `state` in `job.json`. The application owns pickup, stop, continue, and state transitions.
- Do the implementation in the working directory above. Read or write task evidence in the job folder.
- Run git status and git diff in the repository path above when you need to inspect changes.
- Please do not commit or push yourself; the platform commits after review. If you did commit, that is not a problem: it will be shown and cleaned up where safe. Never push to a protected branch or rewrite history that existed before this run.
- Place review-relevant screenshots / result files under the job folder's `results/` directory.
- Label each evidence screenshot's source in its filename: `--real` for a shot against a running backend, `--mocked` for an e2e run with mocked API routes. For UI-acceptance evidence, a `--real` shot against a live backend is recommended; mocked shots stay allowed but must be labelled. Composite / stitched before-after images are welcome - name them `--composite` and list their parts, for example `before-after--composite-real-mocked.png`. Unlabelled filenames make no source claim.
- Make sure every image you link from a protocol or note actually exists under `results/` (or `attachments/`); a link to a missing file is surfaced to the reviewer as a broken-reference finding.
- Do not rely on hand-written `status.md`; the application regenerates it from logs.

Build-time observability (when your change affects product behavior):

- Preserve existing structured logs and event names; do not silently delete instrumentation while editing nearby code.
- For new meaningful behavior, emit structured logs or domain events with stable event names and useful error context, and add timing around expensive or user-visible paths when it would help future debugging or review.
- Skip instrumentation for tiny helpers, pure refactors, doc-only edits, and throwaway code. Observability is not a reason to bloat simple changes.

When you finish, end your reply with one of these tokens on its own line: `[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, `[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]` (rare). Replace the example reason with the actual short reason; never emit the example text unchanged. The orchestrator parses this token to decide what happens next.
