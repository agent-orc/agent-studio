import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskState, type PickupHoldResolution, type PickupHoldStatus, type TaskInfo } from '../../models/task.model';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { NowTickService } from '../../services/now-tick.service';
import { PickupHoldResolutionComponent } from '../pickup-hold-resolution/pickup-hold-resolution.component';
import { RemoteHostsService, claimLoadGateLabel } from '../../features/remote-hosts';

export type PickupHoldVariant = 'card' | 'detail';

/** Mechanism keys as the backend `PickupHoldMechanisms` spells them. */
const MECHANISM_LABELS: Record<string, string> = {
  'dependency-gate': 'Dependency gate',
  'dispatch-rejection': 'Dispatch refused',
  'crash-backoff': 'Crash cooldown',
  'pickup-policy': 'Pickup policy',
  'execution-placement': 'Execution placement',
  'claim-load-gate': 'Host load',
};

/**
 * AGT-2818 - the sentence a card in a pickup lane was missing.
 *
 * A card the pickup gate skips is held, not queued, and before this the board
 * showed only the wait ("waits for release: AGT-2372") or, for a refused
 * dispatch, nothing at all. Two cards sat that way for a month and a week while
 * the reason was recorded in the product and never rendered, which is how the
 * lane came to read as "the system is standing".
 *
 * The projection behind it (`TaskInfo.pickupHold`) is derived server-side from
 * the same facts the runner admission gate consults, so this component states
 * the decision rather than re-deriving it. AGT-3580: every way out carries a
 * typed action, rendered by `<app-pickup-hold-resolution>` as a button, a
 * picker, or a link to the owning setting; a malformed one stays text.
 */
@Component({
  selector: 'app-pickup-hold',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AppTooltipDirective, PickupHoldResolutionComponent],
  templateUrl: './pickup-hold.component.html',
  styleUrl: './pickup-hold.component.scss',
})
export class PickupHoldComponent {
  private readonly remoteHosts = inject(RemoteHostsService, { optional: true });
  private readonly now = inject(NowTickService).now;
  readonly task = input.required<TaskInfo>();
  readonly variant = input<PickupHoldVariant>('card');

  readonly hold = computed<PickupHoldStatus | null>(() => {
    const task = this.task();
    if (task.pickupHold) return task.pickupHold;
    if (task.state !== TaskState.Ready) return null;
    const runnerId = task.executionLocation?.configuredRunnerId;
    if (!runnerId) return null;
    const host = this.remoteHosts?.hosts().find(item =>
      item.clientId === runnerId || item.id === runnerId || item.capacityHostId === runnerId);
    const gate = host?.claimLoadGate;
    if (!gate || gate.state !== 'closed' || !gate.since) return null;
    return {
      classification: 'satisfiable-soon',
      mechanism: 'claim-load-gate',
      reason: `${host.name} is ${claimLoadGateLabel(gate).toLowerCase()}. Running work continues; new claims resume when load falls.`,
      sinceUtc: gate.since,
      unsatisfiable: false,
      resolutions: [],
    };
  });

  /** `open` for an honest wait, `blocked` for a gate that can never open. */
  readonly tone = computed(() => ((this.hold()?.classification === 'unsatisfiable' || this.hold()?.unsatisfiable) ? 'blocked'
    : this.hold()?.classification === 'stalled' ? 'stalled' : 'open'));

  readonly classificationLabel = computed(() => {
    const classification = this.hold()?.classification
      ?? (this.hold()?.unsatisfiable ? 'unsatisfiable' : 'satisfiable-soon');
    return titleCase(classification);
  });

  readonly mechanismLabel = computed(() => {
    const mechanism = this.hold()?.mechanism ?? '';
    return MECHANISM_LABELS[mechanism] ?? titleCase(mechanism);
  });

  /**
   * The headline separates the two states the card conflated: a wait that will
   * end on its own, and a configuration error that will not.
   */
  readonly headline = computed(() => {
    const hold = this.hold();
    if (!hold) return '';
    return hold.classification === 'unsatisfiable' || hold.unsatisfiable
      ? 'Held: this cannot clear by itself'
      : hold.classification === 'stalled'
        ? 'Held: prerequisite needs attention'
      : 'Held: not pickable right now';
  });

  /** "held for 34d" - the number that turns a quiet lane into a standstill. */
  readonly age = computed(() => {
    const hold = this.hold();
    return hold ? `held for ${elapsed(heldSeconds(hold, this.now()))}` : '';
  });

  /** Full calendar instant, disclosed on hover rather than spent inline. */
  readonly sinceTooltip = computed(() => {
    const hold = this.hold();
    if (!hold) return '';
    const parsed = Date.parse(hold.sinceUtc);
    return Number.isNaN(parsed) ? hold.sinceUtc : `Since ${new Date(parsed).toLocaleString('en-US')}`;
  });

  /**
   * The reason, unless the surface already states it in full. A board card
   * renders the durable `remoteDispatchRejection` record itself, right below
   * this block and with the code and instant attached, so repeating the same
   * sentence here would spend a card line saying nothing new. Every other
   * mechanism, and the detail variant, always show it.
   */
  readonly reason = computed(() => {
    const hold = this.hold();
    if (!hold) return null;
    const duplicated = this.variant() === 'card' && hold.mechanism === 'dispatch-rejection';
    return duplicated ? null : hold.reason;
  });

  readonly resolutions = computed(() => this.hold()?.resolutions ?? []);

  readonly resolutionIdentity = (_: number, resolution: PickupHoldResolution): string =>
    `${resolution.kind}:${resolution.sourceKey ?? ''}:${resolution.targetKey ?? ''}`;
}

function titleCase(value: string): string {
  const words = value.split(/[-_\s]+/).filter(Boolean);
  if (words.length === 0) return 'Held';
  return words.map((word, index) =>
    index === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word).join(' ');
}

/** Age at `nowMs`, from the hold start the board card carries. */
function heldSeconds(hold: PickupHoldStatus, nowMs: number): number {
  const since = Date.parse(hold.sinceUtc);
  return Number.isNaN(since) ? hold.heldForSeconds ?? 0 : (nowMs - since) / 1000;
}

/** Coarse on purpose: a hold measured in weeks does not need its seconds. */
function elapsed(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  if (total < 60) return `${total}s`;
  const minutes = Math.floor(total / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h${(minutes % 60).toString().padStart(2, '0')}m`;
  const days = Math.floor(hours / 24);
  return days < 7 ? `${days}d${hours % 24}h` : `${days}d`;
}
