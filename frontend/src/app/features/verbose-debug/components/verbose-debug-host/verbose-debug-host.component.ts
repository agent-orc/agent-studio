import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { VerboseDebugOverlayComponent } from '../verbose-debug-overlay/verbose-debug-overlay.component';
import type { CliOutputLine, TaskInfo } from '../../../../models/task.model';
import type { RunTimeline } from '../../../run-timeline';
import type { TaskScreenshot } from '../../../screenshots';
import type { TaskTokenSummary } from '../../../tokens';

export interface VerboseDebugContext {
  lines: CliOutputLine[];
  runTimeline: RunTimeline | null;
  screenshots: TaskScreenshot[];
  tokenSummary: TaskTokenSummary | null;
  job: TaskInfo | null;
}

/** App-shell host of the verbose debug overlay; keeps the overlay markup out of the root template. */
@Component({
  selector: 'app-verbose-debug-host',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [VerboseDebugOverlayComponent],
  templateUrl: './verbose-debug-host.component.html',
})
export class VerboseDebugHostComponent {
  readonly context = input<VerboseDebugContext | null>(null);
  readonly closeRequest = output<void>();
}
