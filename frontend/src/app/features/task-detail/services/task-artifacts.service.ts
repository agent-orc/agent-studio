import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { TaskArtifact, TaskArtifactsResponse } from '../../../models/task.model';
import { TaskBackgroundPoller } from '../../polling/services/task-background-poller';

/**
 * Job-root Markdown files that are orchestrator/runner machinery rather than
 * user-facing artifacts. They live in the job (or state) folder root next
 * to the real artifacts, so the backend `/artifacts` listing includes
 * them, but they must not count toward — or clutter — the Files tab. Names
 * are matched case-insensitively.
 *
 * This is the full set of machinery `.md` files the backend writes into a
 * task folder root (verified against `backend/` — every other
 * `orchestrator-*.md` / `runner-*.md` name is a prompt template under
 * `prompts/`, never a job-folder file):
 *
 *   - `orchestrator-follow-up.md` — the reissue reason the review
 *     orchestrator writes for the pickup runner (see backend
 *     `ReviewDecisionOrchestrator` / `ProjectRunner`). Surfaced in the
 *     Timeline / decision UI, not a "file" the operator dropped.
 *   - `failed-pickup-reason.md` — why a pickup was abandoned; written into
 *     the `failed-pickup/<slug>/` folder root (see `TaskAccessService`).
 *   - `archive-reason.md` — why a task was archived; written into the
 *     `archive/<slug>/` folder root (see `TaskAccessService`).
 *
 * (`status.md` is already dropped server-side because it owns the
 * Protocol tab, so it never reaches this filter.)
 */
const MACHINERY_ARTIFACT_NAMES = new Set<string>([
  'orchestrator-follow-up.md',
  'failed-pickup-reason.md',
  'archive-reason.md',
]);

function isUserRelevantArtifact(artifact: TaskArtifact): boolean {
  return !MACHINERY_ARTIFACT_NAMES.has(artifact.name.toLowerCase());
}

/**
 * Per-detail Files-tab manifest. Owns the list of user-relevant document
 * artifacts in the job root (prompt + aspect verdicts + code-review + notes +
 * isolated interactive HTML) that the Files tab renders and the Files-tab
 * count badge sums.
 *
 * Internal machinery is kept out of the manifest so the badge counts what
 * an operator would call a "file": subfolders (`logs/`, `results/`,
 * `attachments/`) and non-`.md` state (`lifecycle.json`,
 * `pipeline-execution.json`, `*.jsonl`) are already excluded by the
 * backend `/artifacts` endpoint (top-level Markdown, HTML, and aspect JSON,
 * minus `status.md`); the remaining machinery `.md` files that DO sit in
 * the job/state-folder root — {@link MACHINERY_ARTIFACT_NAMES} — are
 * stripped here in {@link applyResponse}. Without this the badge
 * over-counted (the "kaputt" 9): the orchestrator follow-up file inflated
 * every reissued task's Files count.
 *
 * Polls on a slow cadence so the count stays live while a run generates
 * fresh aspect / code-review files, instead of freezing at the value
 * captured when the task was first opened. Mirrors
 * {@link ScreenshotsPollService}; provided locally on
 * `TaskDetailComponent` (no global state).
 */
@Injectable()
export class TaskArtifactsService extends TaskBackgroundPoller<TaskArtifactsResponse | null> {
  private readonly jobs = inject(TaskService);

  // Files change only when the runner writes a new `.md` into the job
  // root, so a 10 s cadence keeps the count fresh without hammering the
  // backend — same trade-off as the sibling screenshots poll.
  protected readonly intervalMs = 10_000;

  readonly artifacts = signal<TaskArtifact[]>([]);
  readonly loadError = signal(false);

  protected fetch(jobId: string, watchPath: string): Observable<TaskArtifactsResponse | null> {
    return this.jobs.listJobArtifacts(jobId, watchPath);
  }

  protected applyResponse(res: TaskArtifactsResponse | null): void {
    this.loadError.set(false);
    this.artifacts.set((res?.files ?? []).filter(isUserRelevantArtifact));
  }

  protected override onError(): void { this.loadError.set(true); }

  protected clearValue(): void {
    this.loadError.set(false);
    this.artifacts.set([]);
  }
}
