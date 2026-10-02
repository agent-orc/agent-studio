import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import {
  UsageCliChipComponent,
  UsageCostChipComponent,
  UsageDetailSurfaceComponent,
  type UsageDetailFocus,
} from '../../../app/features/usage-cockpit';
import * as F from './usage-chips.fixtures';

/**
 * HUC-S3 fixture harness (`?view=detail`). A stand-in header with the real
 * chips and the real detail surface: the desktop row shows every CLI chip and
 * the cost chip; below 768 px the phone row shows only the primary CLI and
 * cost, as in the Dossier responsive table. The header itself is HUC-S4; this
 * page exists so Playwright can prove open, close, focus and breakpoint
 * behaviour against pinned data.
 */
@Component({
  selector: 'mockup-usage-detail-harness',
  standalone: true,
  imports: [UsageCliChipComponent, UsageCostChipComponent, UsageDetailSurfaceComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-detail-harness.component.html',
  styleUrl: './usage-chips-gallery.component.scss',
})
export class UsageDetailHarnessComponent {
  readonly f = F;
  readonly theme = new URLSearchParams(location.search).get('theme') === 'light' ? 'light' : 'dark';
  readonly detail = signal<UsageDetailFocus | null>(null);

  constructor() {
    document.documentElement.setAttribute('data-studio-theme', this.theme);
  }

  isOpen(focus: UsageDetailFocus): boolean {
    const open = this.detail();
    if (!open || open.kind !== focus.kind) return false;
    return open.kind === 'cost' || (focus.kind === 'cli' && open.cliId === focus.cliId);
  }

  toggle(focus: UsageDetailFocus): void {
    this.detail.set(this.isOpen(focus) ? null : focus);
  }

  cli(cliId: string): UsageDetailFocus {
    return { kind: 'cli', cliId };
  }

  readonly cost: UsageDetailFocus = { kind: 'cost' };
}
