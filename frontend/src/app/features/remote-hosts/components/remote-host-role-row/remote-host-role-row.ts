import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  hostStatusLabel,
  hostStatusTone,
  runnerServiceRoleLabel,
  type HostActionKind,
  type RemoteHost,
} from '../../models/remote-host.model';

/** One runner process role nested below its advertised physical machine. */
@Component({
  // eslint-disable-next-line @angular-eslint/component-selector -- Native table semantics require a tr host.
  selector: 'tr[appRemoteHostRoleRow]',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './remote-host-role-row.html',
  styleUrl: './remote-host-role-row.scss',
  host: {
    'data-testid': 'remote-host-role-row',
    '[attr.data-tone]': 'tone()',
    '[attr.data-retired]': 'retired()',
    '[attr.data-role]': 'host().serviceRole ?? "runner"',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostRoleRowComponent {
  readonly host = input.required<RemoteHost>();
  readonly activeSlots = input(0);
  readonly action = output<{ kind: HostActionKind; id: string }>();

  readonly retired = computed(() => this.host().status === 'retired');
  readonly liveLoading = computed(() => this.host().liveDataState === 'loading');
  readonly tone = computed(() => this.liveLoading() ? 'idle' : hostStatusTone(this.host().status));
  readonly statusLabel = computed(() => this.liveLoading()
    ? 'Loading live status'
    : hostStatusLabel(this.host().status));
  readonly roleLabel = computed(() => runnerServiceRoleLabel(this.host().serviceRole));
  readonly slotTotal = computed(() => roleSlotTotal(this.host()));

  emit(kind: HostActionKind): void {
    if (this.host().busyAction) return;
    this.action.emit({ kind, id: this.host().id });
  }
}

export function roleSlotTotal(host: RemoteHost): number | null {
  if (host.serviceRole === 'review' && host.roleMaxParallelism) return host.roleMaxParallelism;
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  if (host.effectiveMaxParallelism) return host.effectiveMaxParallelism;
  if (host.roleMaxParallelism) return host.roleMaxParallelism;
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    const reported = host.activeTaskCount + host.availableSlots;
    return reported > 0 ? reported : null;
  }
  return null;
}
