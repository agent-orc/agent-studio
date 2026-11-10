import { ChangeDetectionStrategy, Component, ElementRef, input, output, viewChild } from '@angular/core';
import type { PhysicalHostGroup } from '../../models/physical-host-group';
import type { RemoteHost } from '../../models/remote-host.model';
import { roleSlotTotal } from '../../models/role-slots';
import { groupNotEnrolled } from '../../models/host-summary';
import { hostStatusIndicatorTone, hostStatusLabel } from '../../models/remote-host.model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { differsFromStableLabel, type StableReleaseIdentity } from '../../models/host-release-drift';

@Component({
  selector: 'app-environment-host-list', standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './environment-host-list.html', styleUrl: './environment-host-list.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHostListComponent {
  readonly statusTone = hostStatusIndicatorTone;
  readonly statusLabel = hostStatusLabel;
  readonly groups = input.required<readonly PhysicalHostGroup[]>();
  readonly gates = input<readonly { id: string; name: string; busy: number; slots: number; lastHeartbeat: string }[]>([]);
  readonly selected = input<string | null>(null);
  readonly slots = input.required<(host: RemoteHost) => number>();
  readonly now = input<number>(Date.now());
  readonly stableRelease = input<StableReleaseIdentity | null>(null);
  readonly selectedChange = output<string>();
  private readonly list = viewChild<ElementRef<HTMLElement>>('list');
  /** "differs from Stable 3d61e8b" for a machine whose worst role is another build (AGT-3689). */
  differsLabel(group: PhysicalHostGroup): string | null {
    return group.machine.releaseDrift?.state === 'differs' ? differsFromStableLabel(this.stableRelease()) : null;
  }
  hasGroup(name: string): boolean { return this.groups().some(group => group.name === name); }
  notEnrolled(group: PhysicalHostGroup): boolean { return groupNotEnrolled(group, this.now()); }
  notClaiming(group: PhysicalHostGroup): boolean {
    return group.roles.some(role => role.status !== 'retired' && role.claimLoadGate?.state === 'closed');
  }
  capacity(host: RemoteHost): number | null {
    return roleSlotTotal(host);
  }
  groupCapacity(group: PhysicalHostGroup): number | null {
    const values = group.roles.map(role => this.capacity(role));
    return values.every((value): value is number => value !== null)
      ? (values as number[]).reduce((sum, value) => sum + value, 0)
        + this.gates().filter(gate => gate.name === group.name).reduce((sum, gate) => sum + gate.slots, 0)
      : null;
  }
  groupSlots(group: PhysicalHostGroup): number {
    return group.roles.reduce((sum, role) => sum + this.slots()(role), 0)
      + this.gates().filter(gate => gate.name === group.name).reduce((sum, gate) => sum + gate.busy, 0);
  }
  onKeydown(event: KeyboardEvent): void {
    if ((event.target as HTMLElement).closest('input, textarea, select, [contenteditable="true"]')) return;
    const surface = this.list()?.nativeElement;
    if (!surface) return;
    const rows = [...surface.querySelectorAll<HTMLButtonElement>('[data-host-row]')];
    const index = rows.findIndex(row => row === document.activeElement);
    let next: number;
    switch (event.key) {
      case 'ArrowDown': next = Math.min(rows.length - 1, index + 1); break;
      case 'ArrowUp': next = Math.max(0, index - 1); break;
      case 'Home': next = 0; break;
      case 'End': next = rows.length - 1; break;
      case 'PageDown': next = Math.min(rows.length - 1, index + 8); break;
      case 'PageUp': next = Math.max(0, index - 8); break;
      default: return;
    }
    event.preventDefault(); event.stopPropagation();
    if (rows[next]) { rows[next].focus(); this.selectedChange.emit(rows[next].dataset['hostRow']!); }
  }
}
