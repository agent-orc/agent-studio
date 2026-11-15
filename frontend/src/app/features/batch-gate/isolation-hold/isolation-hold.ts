import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import type { BatchIsolationHold } from '../../../models/batch-isolation-hold';
import { BatchPanelComponent } from '../batch-panel/batch-panel';

@Component({
  selector: 'app-isolation-hold', standalone: true, imports: [BatchPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './isolation-hold.html', styleUrl: './isolation-hold.scss',
})
export class IsolationHoldComponent {
  readonly hold = input.required<BatchIsolationHold>();
  readonly project = input.required<string>();
  readonly panelOpen = signal(false);
  readonly closePanel = () => this.panelOpen.set(false);
  dueLabel(value: string): string {
    return `${new Date(value).toLocaleString('en-GB', { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short' })} UTC`;
  }
}
