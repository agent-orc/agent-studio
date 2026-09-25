import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ParsedRuntimeSentinels } from '../runtime-sentinel.parser';
import { terminalSentinelLabel } from '../runtime-sentinel.parser';

@Component({
  selector: 'app-runtime-sentinel-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './runtime-sentinel-view.component.html',
  styleUrl: './runtime-sentinel-view.component.scss',
})
export class RuntimeSentinelViewComponent {
  readonly sentinels = input.required<ParsedRuntimeSentinels>();
  readonly terminalLabel = terminalSentinelLabel;
}
