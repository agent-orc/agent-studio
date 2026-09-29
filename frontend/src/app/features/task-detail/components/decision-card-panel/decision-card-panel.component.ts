import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import type { DecisionContent, DecisionOption, TaskInfo } from '../../../../models/task.model';
import {
  chosenOption,
  deciderName,
  isDecisionCard,
  isDecisionOpen,
  isDecisionOverdue,
  recommendedOption,
} from '../../../../models/decision-card-presentation';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { ClientService } from '../../../../services/client.service';
import { formatDateTimeUtc } from '../../../../services/format.util';
import { NotificationService } from '../../../../services/notification.service';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { studioProjectSlug } from '../../../../services/studio-project-slug.util';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';

/** One settled or reopened entry of the decision's history, ready to render. */
interface DecisionHistoryRow {
  label: string;
  actor: string;
  at: string;
  text: string | null;
}

/**
 * Call to action of a decision card (AGT-2795, Dossier decision-cards §7).
 *
 * The detail opens on the question and the options. Each option shows its
 * consequences, effort, and risk; the recommendation is marked; one "Choose"
 * click records that option with the optional rationale typed above
 * (`POST /api/tasks/{id}/decision`). Once decided the panel shows the recorded
 * option, rationale, decider and time, links the wiki record, and offers a
 * reopen with a required note. Follows the Dossier decision panel's receipt
 * and action vocabulary; renders nothing on any other card kind.
 */
@Component({
  selector: 'app-decision-card-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './decision-card-panel.component.html',
  styleUrl: './decision-card-panel.component.scss',
})
export class DecisionCardPanelComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly clients = inject(ClientService);
  private readonly publicDemo = inject(PublicDemoModeService);
  private readonly taskNavigation = inject(TaskReferenceNavigationService);

  readonly job = input.required<TaskInfo>();
  /** Emitted after a decide or reopen so the host reloads the card. */
  readonly changed = output<void>();

  /** Server response held until the reloaded card arrives. */
  private readonly override = signal<DecisionContent | null>(null);
  readonly decision = computed<DecisionContent | null>(() => this.override() ?? this.job().decision ?? null);
  readonly show = computed(() => isDecisionCard(this.job()) && !!this.decision());
  readonly open = computed(() => isDecisionOpen(this.decision()));
  readonly readOnly = this.publicDemo.readOnly;

  readonly rationale = signal('');
  readonly pendingOptionId = signal<string | null>(null);
  readonly reopening = signal(false);
  readonly reopenNote = signal('');
  readonly reopenPending = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = computed(() => this.pendingOptionId() !== null || this.reopenPending());

  private readonly lookup = (id: string): string | null => this.clients.byId().get(id)?.displayName ?? null;

  readonly decider = computed(() => deciderName(this.decision()?.decider, this.lookup));
  readonly overdue = computed(() => {
    const decision = this.decision();
    return decision ? isDecisionOverdue(decision) : false;
  });
  readonly dueLabel = computed(() => formatDateTimeUtc(this.decision()?.dueDate));
  readonly recommended = computed<DecisionOption | null>(() => {
    const decision = this.decision();
    return decision ? recommendedOption(decision) : null;
  });
  readonly chosen = computed<DecisionOption | null>(() => {
    const decision = this.decision();
    return decision ? chosenOption(decision) : null;
  });
  readonly decidedBy = computed(() => deciderName(this.decision()?.decidedBy?.trim() || this.decision()?.decider, this.lookup));
  readonly decidedAt = computed(() => formatDateTimeUtc(this.decision()?.decidedAt));
  readonly dependants = computed(() => this.decision()?.dependants ?? []);

  readonly recordPath = computed(() => this.decision()?.recordPath?.trim() || null);
  readonly recordHref = computed(() => {
    const path = this.recordPath()?.replace(/^docs\//i, '');
    if (!path) return null;
    return `#/projects/${studioProjectSlug(this.job().projectName)}/wiki?page=${encodeURIComponent(path)}`;
  });

  /** Earlier cycles only: the current decided entry is already the receipt. */
  readonly history = computed<DecisionHistoryRow[]>(() => {
    const decision = this.decision();
    const entries = decision?.history ?? [];
    if (entries.length < 2 && !decision?.reopenNote) return [];
    return entries.map((entry) => {
      const reopened = entry.status === 'reopened';
      const option = decision!.options.find((o) => o.id.toLowerCase() === (entry.optionId ?? '').toLowerCase());
      return {
        label: reopened ? 'Reopened' : `Decided ${option ? `${option.id} · ${option.label}` : entry.optionId ?? ''}`.trim(),
        actor: deciderName(entry.actor, this.lookup),
        at: formatDateTimeUtc(entry.at),
        text: (reopened ? entry.note : entry.rationale)?.trim() || null,
      };
    }).reverse();
  });

  /**
   * Which card and which decision state the panel shows. A string, so a poll
   * that hands over a fresh `TaskInfo` for the same card and status compares
   * equal and does not wipe typed text or the server response held above.
   */
  private readonly cardState = computed(() =>
    `${this.job().taskKey}|${(this.job().decision?.status ?? '').trim().toLowerCase()}`);

  private readonly resetOnCardChange = effect(() => {
    this.cardState();
    this.override.set(null);
    this.rationale.set('');
    this.pendingOptionId.set(null);
    this.reopening.set(false);
    this.reopenNote.set('');
    this.error.set(null);
  });

  isRecommended(option: DecisionOption): boolean {
    return this.recommended()?.id === option.id;
  }

  isChosen(option: DecisionOption): boolean {
    return this.chosen()?.id === option.id;
  }

  updateRationale(event: Event): void {
    this.rationale.set((event.target as HTMLTextAreaElement).value);
  }

  updateReopenNote(event: Event): void {
    this.reopenNote.set((event.target as HTMLTextAreaElement).value);
  }

  choose(option: DecisionOption): void {
    if (this.busy() || this.readOnly() || !this.open()) return;
    const job = this.job();
    this.error.set(null);
    this.pendingOptionId.set(option.id);
    this.tasks.decideCard(job.id, option.id, this.rationale().trim(), job.watchPath).subscribe({
      next: (response) => {
        this.override.set(response.decision);
        this.pendingOptionId.set(null);
        const unblocked = response.decision.dependants ?? [];
        this.notifications.success(unblocked.length > 0
          ? `Decision recorded: ${option.label}. Unblocked ${unblocked.join(', ')}.`
          : `Decision recorded: ${option.label}.`);
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.pendingOptionId.set(null);
        this.error.set(errorMessage(err, 'The decision could not be recorded.'));
      },
    });
  }

  beginReopen(): void {
    this.error.set(null);
    this.reopenNote.set('');
    this.reopening.set(true);
  }

  cancelReopen(): void {
    this.reopening.set(false);
    this.error.set(null);
  }

  confirmReopen(): void {
    const note = this.reopenNote().trim();
    if (!note || this.busy() || this.readOnly()) return;
    const job = this.job();
    this.error.set(null);
    this.reopenPending.set(true);
    this.tasks.reopenDecision(job.id, note, job.watchPath).subscribe({
      next: (response) => {
        this.override.set(response.decision);
        this.reopenPending.set(false);
        this.reopening.set(false);
        this.notifications.success('Decision reopened. Its dependants are blocked again.');
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.reopenPending.set(false);
        this.error.set(errorMessage(err, 'The decision could not be reopened.'));
      },
    });
  }

  openDependant(key: string, event: MouseEvent): void {
    event.preventDefault();
    this.taskNavigation.openReferenceOrNotify(key);
  }
}

/** Backend decision errors carry `{ error, errors: [{ message }] }`. */
function errorMessage(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { error?: string; errors?: { message?: string }[] } | string | null;
    if (typeof body === 'string' && body.trim()) return body;
    if (body && typeof body === 'object') {
      const detail = body.errors?.map((e) => e.message).filter(Boolean).join(' ');
      if (detail) return detail;
      if (body.error) return body.error;
    }
    if (err.status === 403) return 'This decision is assigned to another client or role.';
  }
  return fallback;
}
