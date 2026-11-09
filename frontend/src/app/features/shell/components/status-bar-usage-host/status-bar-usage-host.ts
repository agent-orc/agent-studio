import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { UsageCockpitService } from '../../../usage-cockpit';
import { UsageDetailSurfaceComponent } from '../../../usage-cockpit/lazy';
import type { UsageDetailFocus, UsageDetailRequest } from '../../../usage-cockpit';
import { StatusBarComponent } from '../status-bar/status-bar';

/** Connects the legacy shell's footer to the shared usage detail surface. */
@Component({
  selector: 'app-status-bar-usage-host',
  standalone: true,
  imports: [StatusBarComponent, UsageDetailSurfaceComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './status-bar-usage-host.html',
  styleUrl: './status-bar-usage-host.scss',
})
export class StatusBarUsageHostComponent {
  readonly projectNames = input<string[]>([]);
  readonly usage = inject(UsageCockpitService);
  readonly focus = signal<UsageDetailFocus | null>(null);
  readonly now = signal(Date.now());

  open(request: UsageDetailRequest): void {
    this.now.set(Date.now());
    this.focus.set(request.section === 'cli' && request.cliId
      ? { kind: 'cli', cliId: request.cliId }
      : { kind: 'cost' });
  }
}
