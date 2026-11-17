import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import type { BatchIsolationHold } from '../../../models/batch-isolation-hold';
import { batchDateTime } from '../../../models/batch-gate.model';
import { BatchPanelComponent } from '../batch-panel/batch-panel';

@Component({
  selector: 'app-isolation-hold', standalone: true, imports: [BatchPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './isolation-hold.html', styleUrl: './isolation-hold.scss',
})
export class IsolationHoldComponent {
  readonly hold = input.required<BatchIsolationHold>();
  readonly project = input.required<string>();
  /** AGT-3872: false inside a view that already names the batch time zone once. */
  readonly showZone = input(true);
  readonly panelOpen = signal(false);
  readonly closePanel = () => this.panelOpen.set(false);
  dueLabel(value: string): string {
    const label = batchDateTime(value);
    return this.showZone() ? `${label} UTC` : label;
  }
}
