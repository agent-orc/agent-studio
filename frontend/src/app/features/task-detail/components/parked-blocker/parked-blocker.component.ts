import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';

import type { CliOutputLine, TaskDetail, TaskInfo, TaskPromptHistoryEntry } from '../../../../models/task.model';
import type { RunPromptEntry, RunRecord, RunTimeline } from '../../../../features/run-timeline';
import {
  buildParkedBlockerView,
  type ParkedBlockerView,
} from '../../../../models/parked-blocker-presentation';
import { formatDateTimeUtc } from '../../../../services/format.util';
import { TaskService } from '../../../../services/task.service';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { ActivityLogViewComponent } from '../activity-log-view/activity-log-view';
import { resolveTaskArtifactLink } from '../task-artifact-links/task-artifact-link';
import { TooltipDirective } from 'coding-agent-chat/shared';

const EXPANDED_STORAGE_KEY = 'taskboard.parkedSession.expanded.v1';
const TRANSCRIPT_PAGE_LINES = 250;

export function resolveParkingRun(runs: readonly RunRecord[], parkedAt: string): RunRecord | null {
  if (runs.length === 0) return null;
  const parkedMs = Date.parse(parkedAt);
  if (!Number.isFinite(parkedMs)) return runs.at(-1) ?? null;
  return [...runs]
    .filter(run => Date.parse(run.startedAt) <= parkedMs)
    .sort((a, b) => Date.parse(a.startedAt) - Date.parse(b.startedAt))
    .at(-1) ?? runs.at(-1) ?? null;
}

export function resolveRunPrompt(
  run: RunRecord,
  promptMarkdown: string | null,
  promptHistory: readonly TaskPromptHistoryEntry[],
  promptEntries: readonly RunPromptEntry[],
): string | null {
  if (run.index === 1 && promptMarkdown?.trim()) return promptMarkdown.trim();
  const history = promptHistory.find(item => item.index === run.index - 1)?.markdown?.trim();
  if (history) return history;
  if (run.userFollowup?.trim()) return run.userFollowup.trim();
  return promptEntries.find(item => item.runIndex === run.index)?.promptPreview?.trim() || null;
}

/**
 * The authoritative statement about why a parked card is not moving (AGT-2816).
 *
 * AGT-2736 was parked for three days on an operator decision and the card said
 * `Result: Success` with `Open Items: None`: the park reason was on disk and no
 * component rendered it, so the escalation summary derived its headline from the
 * run's own status stub and repeated the run's "Success".
 *
 * This panel renders ABOVE the derived escalation headline because it outranks
 * it: what the run parked itself on is a fact, the headline is an inference. It
 * shows the park type, the question (never the slug in its place), the options
 * the run had already weighed, what would clear the park, the latest recall
 * verdict, and the documents the run named.
 */
@Component({
  selector: 'app-parked-blocker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, ActivityLogViewComponent, DisclosureMarkerComponent],
  templateUrl: './parked-blocker.component.html',
  styleUrl: './parked-blocker.component.scss',
})
export class ParkedBlockerComponent {
  readonly detail = input.required<TaskDetail>();

  private readonly tabs = inject(StudioTabStateService);
  private readonly tasks = inject(TaskService);
  private loadedTaskId: string | null = null;
  private readonly expandedByTask = signal<Record<string, boolean>>(readExpandedMap());

  readonly info = computed<TaskInfo>(() => this.detail().info);
  readonly sessionExpanded = computed(() => this.expandedByTask()[this.info().id] === true);
  readonly sessionLoading = signal(false);
  readonly sessionError = signal<string | null>(null);
  readonly sessionRun = signal<RunRecord | null>(null);
  readonly sessionPrompt = signal<string | null>(null);
  readonly transcriptLines = signal<CliOutputLine[]>([]);
  readonly visibleLineCount = signal(TRANSCRIPT_PAGE_LINES);

  readonly visibleTranscriptLines = computed(() => {
    const lines = this.transcriptLines();
    return lines.slice(Math.max(0, lines.length - this.visibleLineCount()));
  });
  readonly earlierLineCount = computed(() =>
    Math.max(0, this.transcriptLines().length - this.visibleTranscriptLines().length));

  readonly fullRunLogHref = computed(() => resolveTaskArtifactLink('logs/cli-output.log', {
    jobId: this.info().id,
    watchPath: this.info().watchPath,
  })?.href ?? null);

  readonly pinnedDifferences = computed(() => {
    const info = this.info();
    const run = this.sessionRun();
    if (!run) return [];
    const rows: { label: string; value: string }[] = [];
    if (info.cliType && run.cli && info.cliType !== run.cli) rows.push({ label: 'Pinned CLI', value: info.cliType });
    if (info.modelExplicit === true && info.model && info.model !== run.model) {
      rows.push({ label: 'Pinned model', value: info.model });
    }
    if (info.thinkingLevelExplicit === true && info.thinkingLevel && info.thinkingLevel !== run.thinkingLevel) {
      rows.push({ label: 'Pinned thinking', value: info.thinkingLevel });
    }
    return rows;
  });

  private readonly taskChangeEffect = effect(() => {
    const id = this.info().id;
    if (id === this.loadedTaskId) return;
    this.loadedTaskId = id;
    this.resetSession();
    if (this.sessionExpanded()) this.loadSessionExcerpt();
  });

  /** Null when the card is not parked; the template renders nothing then. */
  readonly view = computed<ParkedBlockerView | null>(() => buildParkedBlockerView(this.info()));

  readonly parkedAtLabel = computed(() => {
    const view = this.view();
    return view ? formatDateTimeUtc(view.parkedAt) : '';
  });

  /**
   * How long the current sweep verdict has held, plus why it reached it. The
   * marker records when a verdict was FIRST observed and an unchanged verdict is
   * never re-persisted, so this reads as "unchanged for 3 days" rather than
   * claiming the sweep last ran then.
   */
  readonly evaluationLine = computed(() => {
    const recall = this.view()?.recall;
    if (!recall) return '';
    const when = recall.heldFor
      ? `unchanged for ${recall.heldFor}`
      : 'no sweep has evaluated this blocker yet';
    return recall.detail ? `${when} · ${recall.detail}` : when;
  });

  /** The freetext park reason, verbatim - it is the record, not a headline. */
  readonly reasonLine = computed(() => this.info().parkedBlocker?.reason?.trim() || 'not recorded');

  toggleSession(): void {
    const id = this.info().id;
    const expanded = !this.sessionExpanded();
    this.expandedByTask.update(map => ({ ...map, [id]: expanded }));
    writeExpandedMap(this.expandedByTask());
    if (expanded) this.loadSessionExcerpt();
  }

  showEarlier(): void {
    this.visibleLineCount.update(count => count + TRANSCRIPT_PAGE_LINES);
  }

  private loadSessionExcerpt(): void {
    if (this.sessionLoading() || this.sessionRun() || this.sessionError()) return;
    const info = this.info();
    this.sessionLoading.set(true);
    forkJoin({
      timeline: this.tasks.getRunTimeline(info.id, info.watchPath),
      output: this.tasks.getJobOutput(info.id, info.watchPath),
    }).subscribe({
      next: ({ timeline, output }) => this.applySessionExcerpt(timeline, output),
      error: () => {
        this.sessionLoading.set(false);
        this.sessionError.set('Session context could not be loaded. Open the full run log instead.');
      },
    });
  }

  private applySessionExcerpt(timeline: RunTimeline, output: CliOutputLine[]): void {
    const view = this.view();
    const run = resolveParkingRun(timeline.runs ?? [], view?.parkedAt ?? '');
    this.sessionRun.set(run);
    this.sessionPrompt.set(run ? resolveRunPrompt(
      run,
      this.detail().promptMarkdown,
      this.detail().promptHistory ?? [],
      timeline.promptEntries ?? [],
    ) : null);
    if (run?.lineStart != null) {
      const start = Math.max(0, run.lineStart - 1);
      const end = Math.min(output.length, run.lineEnd ?? output.length);
      this.transcriptLines.set(output.slice(start, Math.max(start, end)));
    } else {
      this.transcriptLines.set([]);
    }
    this.sessionLoading.set(false);
  }

  private resetSession(): void {
    this.sessionLoading.set(false);
    this.sessionError.set(null);
    this.sessionRun.set(null);
    this.sessionPrompt.set(null);
    this.transcriptLines.set([]);
    this.visibleLineCount.set(TRANSCRIPT_PAGE_LINES);
  }

  /** Open a document the parking run named, in the project wiki. */
  openDocument(relPath: string): void {
    const projectName = this.info().projectName;
    if (!projectName) return;
    this.tabs.open({
      kind: 'hub',
      projectName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath },
    });
  }
}

function readExpandedMap(): Record<string, boolean> {
  try {
    return JSON.parse(localStorage.getItem(EXPANDED_STORAGE_KEY) ?? '{}') as Record<string, boolean>;
  } catch { return {}; }
}

function writeExpandedMap(map: Record<string, boolean>): void {
  try {
    localStorage.setItem(EXPANDED_STORAGE_KEY, JSON.stringify(map));
  } catch { /* Browser storage is best-effort. */ }
}
