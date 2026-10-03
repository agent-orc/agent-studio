import { ChangeDetectionStrategy, Component, signal } from '@angular/core';

import {
  UsageCliChipComponent,
  UsageCostChipComponent,
  UsageSlotChipComponent,
} from '../../../app/features/usage-cockpit';
import * as F from './usage-chips.fixtures';

/**
 * HUC-S2 fixture harness. Mounts the real usage chip components with pinned
 * synthetic data so Playwright can check geometry, accessible names, focus
 * and state labels in both themes. This is not the header integration
 * (HUC-S4); each row is a plain flex row on the title-bar surface.
 *
 * `?theme=light` or `?theme=dark` selects the palette through
 * `html[data-studio-theme]`, the same switch the app uses.
 */
@Component({
  selector: 'mockup-usage-chips-gallery',
  standalone: true,
  imports: [UsageCliChipComponent, UsageCostChipComponent, UsageSlotChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-chips-gallery.component.html',
  styleUrl: './usage-chips-gallery.component.scss',
})
export class UsageChipsGalleryComponent {
  readonly f = F;
  readonly theme = new URLSearchParams(location.search).get('theme') === 'light' ? 'light' : 'dark';
  readonly openCli = signal<string | null>(null);
  readonly costOpen = signal(false);
  readonly slotsOpen = signal(false);

  constructor() {
    document.documentElement.setAttribute('data-studio-theme', this.theme);
  }

  toggleCli(id: string): void {
    this.openCli.update(current => (current === id ? null : id));
  }
}
