import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { WatchPathEntry } from '../../../../models/task.model';
import type { TaskScreenshot } from '../../../screenshots';
import { WorkspaceOverlaysComponent } from '../workspace-overlays/workspace-overlays.component';

/** Loads workspace settings only after its route or legacy overlay opens. */
@Component({
  selector: 'app-workspace-overlays-host',
  standalone: true,
  imports: [WorkspaceOverlaysComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-overlays-host.component.html',
  styleUrl: './workspace-overlays-host.component.scss',
})
export class WorkspaceOverlaysHostComponent {
  readonly open = input(false);
  readonly inline = input(false);
  readonly watchPaths = input<readonly WatchPathEntry[]>([]);
  readonly openTask = output<TaskScreenshot>();
  readonly openProjectSettings = output<string>();
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();
}
