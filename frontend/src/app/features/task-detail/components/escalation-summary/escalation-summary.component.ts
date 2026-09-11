import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { TaskState } from '../../../../models/task.model';
import type { TaskDetail, TaskInfo } from '../../../../models/task.model';
import { CodeReviewListEntry, TaskService } from '../../../../services/task.service';
import { TaskTimelinePollService } from '../../../polling/services/task-timeline-poll.service';
import {
  isSteeringKind,
  steeringInfoFromEvent,
  type SteeringInfo,
} from '../../../../components/steering-detail';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { EscalationDetailsComponent } from '../escalation-details/escalation-details.component';
import {
  laneActionsFor,
  type TriageActionPayload,
  type TriageButton,
} from '../../state/triage-actions.model';
import {
  buildEscalationSummaryView,
  type EscalationSummaryView,
} from './escalation-summary.util';

/**
 * Escalation summary panel (AGT-2019). Renders — prominently, above the panes —
 * for a `5e-escalated` card the four things an operator needs to make the call
 * that the thin last-run status protocol never showed: the open gate points,
 * the code-review verdict, the delivery context (already in develop?), and the
 * gate's recommendation. Pure display aggregation of existing artifacts; see
 * {@link buildEscalationSummaryView} for the source-priority rules.
 *
 * The panel owns two cheap fetches (the code-review grade list and the reissue
 * follow-up file) and reads the already-polled task timeline for the steering
 * event; everything else comes off `detail()`. A NeedsInput panel also owns
 * the bounded answer submission through the existing steer continuation API.
 */
@Component({
  selector: 'app-escalation-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, PendingButtonDirective, EscalationDetailsComponent],
  templateUrl: './escalation-summary.component.html',
  styleUrl: './escalation-summary.component.scss',
})
export class EscalationSummaryComponent {
  readonly detail = input.required<TaskDetail>();
  readonly mutationsBlocked = input(false);
  readonly triageActingId = input<string | null>(null);
  readonly triageAction = output<TriageActionPayload>();
  readonly answerDraft = signal('');
  readonly answering = signal(false);
  readonly answerError = signal<string | null>(null);

  /** The three terminal operator choices shown beside NEEDS DECISION. */
  readonly decisionActions = computed(() =>
    this.detail().info.state === TaskState.Escalated
      ? laneActionsFor(TaskState.Escalated).filter((action) => DECISION_ACTION_IDS.has(action.id))
      : [],
  );

  private readonly jobs = inject(TaskService);
  private readonly timelinePoll = inject(TaskTimelinePollService);

  /** Newest-first code-review list for the open job; drives the verdict head. */
  readonly codeReviews = signal<CodeReviewListEntry[]>([]);
  /** Body of `orchestrator-follow-up.md`, or null when the file is absent. */
  readonly followUpMarkdown = signal<string | null>(null);
  /** Which job the current fetch results belong to, to drop stale responses. */
  private fetchedJobId: string | null = null;

  /**
   * Collapse state of the whole panel, remembered per task (AGT-2060). The
   * header is the click target; collapsing hides the reason line, gate
   * checklist and detail grid so the panel stops crowding out the rest of the
   * task-detail view. Default follows the acute-vs-history rule (AGT-2049): an
   * acute `5e-escalated` card opens (the operator is here to act on it), every
   * other lane where an escalation lingers (a card parked in `5-human-review`
   * with an escalate verdict) starts closed (historical context). An explicit
   * per-task toggle, once made, always wins over the lane default.
   */
  readonly collapsed = signal<boolean>(false);
  /** Job the current collapse state was seeded for, to re-seed on task change. */
  private collapseJobId: string | null = null;

  constructor() {
    // Seed the collapse state whenever the open task changes: the stored
    // per-task preference wins; absent that, the lane picks the default.
    effect(() => {
      const info = this.detail().info;
      if (info.id === this.collapseJobId) return;
      this.collapseJobId = info.id;
      this.collapsed.set(initialCollapsed(info));
    });

    // Re-fetch the grade list + follow-up file whenever the open job changes.
    // Both are best-effort: a missing follow-up file (the common pure-escalation
    // case) or an empty review list simply collapses that section.
    effect(() => {
      const info = this.detail().info;
      if (info.id === this.fetchedJobId) return;
      this.fetchedJobId = info.id;
      this.codeReviews.set([]);
      this.followUpMarkdown.set(null);

      this.jobs.listCodeReviews(info.id, info.watchPath).subscribe({
        next: (resp) => {
          if (this.fetchedJobId !== info.id) return;
          this.codeReviews.set(resp.entries ?? []);
        },
        error: () => {
          /* leave empty; the verdict head hides when there is nothing to show */
        },
      });

      this.jobs.readJobFile(info.id, 'orchestrator-follow-up.md', info.watchPath).subscribe({
        next: (text) => {
          if (this.fetchedJobId !== info.id) return;
          this.followUpMarkdown.set(text ?? null);
        },
        error: () => {
          if (this.fetchedJobId !== info.id) return;
          // 404 is expected on a pure-escalation card — the follow-up file is a
          // reissue artifact. Fall back to the timeline / evidence sources.
          this.followUpMarkdown.set(null);
        },
      });
    });
  }

  /**
   * Latest steering step from the already-polled task timeline. Mirrors the
   * Overview pane's derivation so the escalation reason + structured gate
   * findings read the same everywhere. Null until a steering event exists.
   */
  private readonly steering = computed<SteeringInfo | null>(() => {
    const events = this.timelinePoll.events();
    for (let i = events.length - 1; i >= 0; i--) {
      if (isSteeringKind(events[i].kind)) return steeringInfoFromEvent(events[i]);
    }
    return null;
  });

  /** The aggregated view model the template renders. */
  readonly view = computed<EscalationSummaryView>(() =>
    buildEscalationSummaryView({
      info: this.detail().info,
      reviewEvidence: this.detail().reviewEvidence ?? [],
      codeReviews: this.codeReviews(),
      steering: this.steering(),
      statusMarkdown: this.detail().statusMarkdown,
      timeline: this.timelinePoll.events(),
    }),
  );

  /**
   * DtC step 6 — header title. A GaveUpToHuman escalation says so plainly
   * ("Orchestrator gave up"), reading distinctly from a logical / quality
   * escalation ("Escalation") a human judges on its merits.
   */
  readonly headTitle = computed<string>(() =>
    this.detail().info.needsInput
      ? (this.hasQueuedAnswer() ? 'Answer queued' : 'Agent needs input')
      : this.view().escalation?.kind === 'gave-up' ? 'Orchestrator gave up' : 'Escalation',
  );

  readonly hasQueuedAnswer = computed(() =>
    this.detail().info.pendingIntent?.mode === 'steer'
      && !!this.detail().info.pendingIntent?.prompt?.trim(),
  );

  updateAnswer(event: Event): void {
    this.answerDraft.set((event.target as HTMLTextAreaElement).value);
  }

  submitAnswer(): void {
    const answer = this.answerDraft().trim();
    const info = this.detail().info;
    if (!answer || this.answering() || this.mutationsBlocked()) return;
    this.answering.set(true);
    this.answerError.set(null);
    this.jobs.continueJob(
      info.id,
      answer,
      info.watchPath,
      undefined,
      undefined,
      undefined,
      'steer',
    ).subscribe({
      next: () => {
        this.answering.set(false);
        this.answerDraft.set('');
      },
      error: (error: { error?: { error?: string }; message?: string }) => {
        this.answering.set(false);
        this.answerError.set(error?.error?.error ?? error?.message ?? 'The answer could not be queued.');
      },
    });
  }

  /** Toggle the panel open/closed and persist the choice for this task. */
  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    writeCollapsePref(this.collapseJobId, next);
  }

  /** Route inline decisions through the detail view's existing triage pipeline. */
  triggerDecision(action: TriageButton): void {
    if (this.mutationsBlocked() || this.triageActingId() !== null) return;
    this.triageAction.emit({ id: action.id, label: action.label, intent: action.intent });
  }

}

/** localStorage key holding the per-task collapse map (`{ [jobId]: boolean }`). */
const COLLAPSE_KEY = 'taskboard.escalation.collapsed';
const DECISION_ACTION_IDS: ReadonlySet<string> = new Set([
  'reissue-escalated',
  'accept-escalated',
  'discard-escalated',
]);

/**
 * Initial collapse for a freshly-opened task: an explicit stored preference
 * wins; otherwise the lane decides. Only the acute `5e-escalated` lane opens by
 * default — everywhere else an escalation is historical context and starts
 * closed.
 */
function initialCollapsed(info: TaskInfo): boolean {
  const stored = readCollapsePref(info.id);
  if (stored !== null) return stored;
  return info.needsInput ? false : info.state !== TaskState.Escalated;
}

function readCollapseMap(): Record<string, boolean> {
  try {
    const raw = localStorage.getItem(COLLAPSE_KEY);
    if (!raw) return {};
    const parsed: unknown = JSON.parse(raw);
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, boolean>) : {};
  } catch {
    return {};
  }
}

/** Stored collapse preference for a job, or null when the operator never set one. */
function readCollapsePref(jobId: string): boolean | null {
  const value = readCollapseMap()[jobId];
  return typeof value === 'boolean' ? value : null;
}

function writeCollapsePref(jobId: string | null, value: boolean): void {
  if (!jobId) return;
  try {
    const map = readCollapseMap();
    map[jobId] = value;
    localStorage.setItem(COLLAPSE_KEY, JSON.stringify(map));
  } catch {
    /* ignore quota / privacy-mode errors — collapse still works in-memory */
  }
}
