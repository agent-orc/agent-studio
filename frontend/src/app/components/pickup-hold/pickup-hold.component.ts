import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { PickupHoldStatus, TaskInfo } from '../../models/task.model';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';

export type PickupHoldVariant = 'card' | 'detail';

/** Mechanism keys as the backend `PickupHoldMechanisms` spells them. */
const MECHANISM_LABELS: Record<string, string> = {
  'dependency-gate': 'Dependency gate',
  'dispatch-rejection': 'Dispatch refused',
  'epic-container': 'Epic container',
  'crash-backoff': 'Crash cooldown',
  'pickup-policy': 'Pickup policy',
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
 * the decision rather than re-deriving it. It offers the ways out and never
 * takes one: releasing a validation gate is an operator decision about whether
 * the validation still has to happen.
 */
@Component({
  selector: 'app-pickup-hold',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AppTooltipDirective],
  templateUrl: './pickup-hold.component.html',
  styleUrl: './pickup-hold.component.scss',
})
export class PickupHoldComponent {
  readonly task = input.required<TaskInfo>();
  readonly variant = input<PickupHoldVariant>('card');

  readonly hold = computed<PickupHoldStatus | null>(() => this.task().pickupHold ?? null);

  /** `open` for an honest wait, `blocked` for a gate that can never open. */
  readonly tone = computed(() => (this.hold()?.unsatisfiable ? 'blocked' : 'open'));

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
    return hold.unsatisfiable
      ? 'Held: this cannot clear by itself'
      : 'Held: not pickable right now';
  });

  /** "held for 34d" - the number that turns a quiet lane into a standstill. */
  readonly age = computed(() => {
    const hold = this.hold();
    return hold ? `held for ${elapsed(hold.heldForSeconds)}` : '';
  });

  /** Full calendar instant, disclosed on hover rather than spent inline. */
  readonly sinceTooltip = computed(() => {
    const hold = this.hold();
    if (!hold) return '';
    const parsed = Date.parse(hold.sinceUtc);
    return Number.isNaN(parsed) ? hold.sinceUtc : `Since ${new Date(parsed).toLocaleString()}`;
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

  readonly resolutionKind = (_: number, resolution: { kind: string }): string => resolution.kind;
}

function titleCase(value: string): string {
  const words = value.split(/[-_\s]+/).filter(Boolean);
  if (words.length === 0) return 'Held';
  return words.map((word, index) =>
    index === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word).join(' ');
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
