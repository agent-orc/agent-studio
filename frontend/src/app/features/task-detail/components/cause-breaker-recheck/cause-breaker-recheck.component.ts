import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import {
  CauseBreakerService, type CauseBreakerCauseView, type CauseBreakerHeldCard,
} from '../../../../services/cause-breaker.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { PendingButtonDirective } from '../../../../components/async-feedback/pending-button.directive';

/** While a re-check runs, its outcome is read again at this pace. */
const RUNNING_REFRESH_MS = 15_000;

/**
 * AGT-3617 (playthrough AGT-3560, finding H1): the intervention card of an
 * open cause breaker names the cards held behind it and offers one primary
 * action, "Re-check now". The re-check releases the newest waiting delivery,
 * so a fixed delivery clears the breaker; a held card's own row lets the
 * owner re-check that card instead. Before this, no UI reached
 * `/api/cause-breakers`, and the probe always took the oldest, still broken
 * delivery.
 */
@Component({
  selector: 'app-cause-breaker-recheck',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './cause-breaker-recheck.component.html',
  styleUrl: './cause-breaker-recheck.component.scss',
})
export class CauseBreakerRecheckComponent {
  readonly job = input.required<TaskInfo>();

  readonly breaker = signal<CauseBreakerCauseView | null>(null);
  /** The task key of the re-check being requested, or `''` for the default card. */
  readonly pending = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly held = computed(() => this.breaker()?.held ?? []);
  readonly firstHeld = computed(() => this.held().find(card => card.recheckFirst) ?? this.held()[0] ?? null);
  readonly probeRunning = computed(() => !!this.breaker()?.probeTaskKey);
  readonly canRecheck = computed(() => {
    const breaker = this.breaker();
    return !!breaker && !breaker.resolving && !breaker.probeTaskKey
      && this.held().length > 0 && this.pending() === null;
  });

  /**
   * Only an open card can own a breaker. The core-first detail does not carry
   * `creationSource`, so every open card asks; the answer is an in-memory
   * lookup that is null for a card that holds nothing.
   */
  private readonly causeKey = computed(() => {
    const job = this.job();
    if (job.state === TaskState.Completed || job.state === TaskState.Archive) return null;
    return job.key || job.id || null;
  });

  private readonly breakers = inject(CauseBreakerService);
  private readonly navigation = inject(TaskReferenceNavigationService);
  private refreshTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect(() => {
      const key = this.causeKey();
      this.error.set(null);
      if (key) this.load(key);
      else this.breaker.set(null);
    });
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  recheck(card?: CauseBreakerHeldCard): void {
    const breaker = this.breaker();
    if (!breaker || !this.canRecheck()) return;
    this.pending.set(card?.taskKey ?? '');
    this.error.set(null);
    this.breakers.recheck(breaker.fingerprint, card?.taskKey).subscribe({
      next: () => {
        this.pending.set(null);
        this.reload();
      },
      error: err => {
        this.pending.set(null);
        this.error.set(typeof err?.error?.message === 'string' ? err.error.message
          : 'The re-check could not be requested. Try again in a minute.');
        this.reload();
      },
    });
  }

  open(key: string): void {
    this.navigation.openReferenceOrNotify(key);
  }

  delivered(card: CauseBreakerHeldCard): string {
    const at = Date.parse(card.deliveredAt ?? card.since);
    if (Number.isNaN(at)) return '';
    const stamp = new Date(at).toLocaleString('en-US', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    return card.deliveredAt ? `delivered ${stamp}` : `held since ${stamp}`;
  }

  private reload(): void {
    const key = this.causeKey();
    if (key) this.load(key);
  }

  private load(key: string): void {
    this.clearTimer();
    this.breakers.forCause(key).subscribe({
      next: breaker => {
        if (this.causeKey() !== key) return;
        this.breaker.set(breaker);
        if (breaker?.probeTaskKey || breaker?.resolving)
          this.refreshTimer = setTimeout(() => this.reload(), RUNNING_REFRESH_MS);
      },
      error: () => {
        if (this.causeKey() === key) this.breaker.set(null);
      },
    });
  }

  private clearTimer(): void {
    if (this.refreshTimer !== null) clearTimeout(this.refreshTimer);
    this.refreshTimer = null;
  }
}
