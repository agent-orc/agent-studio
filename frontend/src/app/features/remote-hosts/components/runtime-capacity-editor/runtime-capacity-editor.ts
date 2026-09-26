import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type {
  HostProjectSlots,
  RemoteChatUsage,
  HostRampStrategy,
  RemoteHost,
} from '../../models/remote-host.model';

export interface RuntimeCapacityChange {
  id: string;
  maxParallelism: number;
  targetLoadPercent: number;
  rampStrategy: HostRampStrategy;
}

export interface HostProjectPolicyChange {
  id: string;
  allowAllProjects: boolean;
  allowedProjectIds: readonly string[];
  expectedVersion: number;
}

/** Ceiling proposed in the empty state when the daemon reported nothing either. */
const SUGGESTED_CEILING = 2;
const SUGGESTED_TARGET_LOAD = 80;

/**
 * The host row's capacity section: the slot ledger, the per-project breakdown of
 * who is spending those slots, and the three editable targets (ceiling, target
 * load, ramp).
 *
 * The ledger total is always the central ceiling. It used to be derived from the
 * daemon's reported free slots, which made the total breathe with the claims
 * ("7 active / 1 free", then "2 active / 1 free") and so never described a
 * capacity at all (AGT-2302). Without a ceiling the component says so instead of
 * inventing a total from telemetry - but it then offers to set one, because a
 * host that nobody ever gave a ceiling is a starting point, not an error state.
 *
 * Occupancy comes from exactly one place: the board's lease truth, passed in as
 * {@link boardActiveSlots}, which is the same derivation the per-project rows
 * are summed from. Mixing it with the daemon's own activeTaskCount let the
 * header say "3 active" while the rows below added up to 2.
 */
@Component({
  selector: 'app-runtime-capacity-editor',
  standalone: true,
  imports: [DatePipe, PendingButtonDirective],
  templateUrl: './runtime-capacity-editor.html',
  styleUrl: './runtime-capacity-editor.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RuntimeCapacityEditorComponent {
  readonly host = input.required<RemoteHost>();
  /**
   * Slots this host holds according to the board's lease truth. The single
   * source for both the ledger and {@link projectSlots}: the panel derives both
   * from one snapshot, so the header total and the rows can never disagree.
   */
  readonly boardActiveSlots = input(0);
  /** Projects occupying this host's slots, derived from the board's lease truth. */
  readonly projectSlots = input<readonly HostProjectSlots[]>([]);
  readonly chatUsage = input<readonly RemoteChatUsage[]>([]);
  readonly capacityChange = output<RuntimeCapacityChange>();
  readonly projectPolicyChange = output<HostProjectPolicyChange>();
  readonly capacityDraft = signal<number | null>(null);
  readonly targetLoadDraft = signal<number | null>(null);
  readonly rampDraft = signal<HostRampStrategy | null>(null);
  readonly allowAllProjectsDraft = signal<boolean | null>(null);
  readonly allowedProjectIdsDraft = signal<string | null>(null);

  /** The hard ceiling, or null when no server has published one yet. */
  readonly ceiling = computed(() => this.host().runtimeCapacity?.maxParallelism ?? null);
  readonly activeSlots = computed(() => Math.max(0, this.boardActiveSlots()));
  readonly activeChats = computed(() => this.chatUsage().reduce((sum, row) => sum + row.activeTurns, 0));
  readonly heavyChats = computed(() => this.chatUsage().reduce((sum, row) => sum + row.heavyTurns, 0));
  /**
   * Pre-fill for the empty state: what the daemon says it runs today, so the
   * first ceiling an operator publishes describes the host rather than a guess.
   */
  readonly suggestedCeiling = computed(() =>
    this.host().effectiveMaxParallelism ?? SUGGESTED_CEILING);
  readonly suggestedTargetLoad = SUGGESTED_TARGET_LOAD;
  readonly freeSlots = computed(() => {
    const ceiling = this.ceiling();
    return ceiling === null ? 0 : Math.max(0, ceiling - this.activeSlots() - this.heavyChats());
  });
  readonly slotsLabel = computed(() => {
    const ceiling = this.ceiling();
    if (ceiling !== null && this.heavyChats() > 0
        && this.activeSlots() + this.heavyChats() > ceiling)
      return `${ceiling} slots, ${this.activeSlots()} coding, ${this.heavyChats()} heavy chat ${this.heavyChats() === 1 ? 'turn' : 'turns'} running alongside, 0 free`;
    if (ceiling !== null && this.heavyChats() > 0)
      return `${ceiling} slots, ${this.activeSlots()} coding, ${this.heavyChats()} taken by ${this.heavyChats() === 1 ? 'a heavy chat turn' : 'heavy chat turns'}`;
    return ceiling === null
      ? `${this.activeSlots()} active / capacity not reported`
      : `${this.activeSlots()} active / ${this.freeSlots()} free / ${ceiling} total`;
  });
  /** Slots held by projects the board can name; the rest are unattributed. */
  readonly attributedSlots = computed(() =>
    this.projectSlots().reduce((sum, entry) => sum + entry.activeSlots, 0));

  readonly awaitingAdoption = computed(() => {
    const host = this.host();
    if (!host.runtimeCapacity) return false;
    return host.runtimeCapacity.version >= 1
      ? host.runtimeCapacity.version !== host.runtimeCapacityAppliedVersion
      : host.runtimeCapacity.maxParallelism !== host.effectiveMaxParallelism;
  });

  updateCapacityDraft(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.capacityDraft.set(Number.isInteger(value) ? value : null);
  }

  updateTargetLoadDraft(event: Event): void {
    const value = Number((event.target as HTMLInputElement).value);
    this.targetLoadDraft.set(Number.isInteger(value) ? value : null);
  }

  updateRampDraft(event: Event): void {
    this.rampDraft.set((event.target as HTMLSelectElement).value as HostRampStrategy);
  }

  updateProjectPolicyMode(event: Event): void {
    this.allowAllProjectsDraft.set(
      (event.target as HTMLSelectElement).value === 'all');
  }

  updateAllowedProjects(event: Event): void {
    this.allowedProjectIdsDraft.set((event.target as HTMLInputElement).value);
  }

  saveProjectPolicy(): void {
    const host = this.host();
    if (host.busyAction || !host.capacityHostId) return;
    const policy = host.projectPolicy;
    const allowAllProjects = this.allowAllProjectsDraft()
      ?? policy?.allowAllProjects
      ?? true;
    const allowedProjectIds = allowAllProjects
      ? []
      : [...new Set((this.allowedProjectIdsDraft()
        ?? policy?.allowedProjectIds.join(', ')
        ?? '')
        .split(',')
        .map(projectId => projectId.trim())
        .filter(Boolean))].sort();
    this.projectPolicyChange.emit({
      id: host.id,
      allowAllProjects,
      allowedProjectIds,
      expectedVersion: policy?.version ?? 0,
    });
    this.allowAllProjectsDraft.set(null);
    this.allowedProjectIdsDraft.set(null);
  }

  /**
   * Apply the drafted targets. Also the write path of the empty state: with no
   * published record the drafts fall back to the daemon-reported suggestion, so
   * the operator's click is what declares the capacity - the server still never
   * invents one on its own.
   */
  save(): void {
    const host = this.host();
    const capacity = host.runtimeCapacity;
    if (host.busyAction) return;
    const maxParallelism = this.capacityDraft()
      ?? capacity?.maxParallelism ?? this.suggestedCeiling();
    const targetLoadPercent = this.targetLoadDraft()
      ?? capacity?.targetLoadPercent ?? SUGGESTED_TARGET_LOAD;
    const rampStrategy = this.rampDraft() ?? capacity?.rampStrategy ?? 'balanced';
    if (!Number.isInteger(maxParallelism) || maxParallelism < 1 || maxParallelism > 256
      || !Number.isInteger(targetLoadPercent)
      || targetLoadPercent < 50
      || targetLoadPercent > 95) return;
    this.capacityChange.emit({
      id: host.id,
      maxParallelism,
      targetLoadPercent,
      rampStrategy,
    });
    this.capacityDraft.set(null);
    this.targetLoadDraft.set(null);
    this.rampDraft.set(null);
  }
}
