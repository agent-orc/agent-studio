import { SegmentedControlComponent } from '../../../../components/segmented-control/segmented-control.component';
import { ChangeDetectionStrategy, Component, model } from '@angular/core';
import type { TaskMode } from '../../../../models/task.model';
import { TooltipDirective } from 'coding-agent-chat/shared';

interface ModeOption {
  readonly value: TaskMode;
  readonly label: string;
  readonly hint: string;
}

/**
 * Create-dialog Mode selector plus the "Allow web access" toggle. `coding`
 * is the read-write default; `planning` and `research` are report-only, while
 * `concept` writes one bounded docs Dossier. Per
 * Decision 2 of the task-modes design the web toggle has a per-mode default
 * (research = on, everything else = off) but stays a single control the user
 * can override. Choosing a mode resets the toggle to that mode's default.
 *
 * Extracted into its own component so the create-task-dialog stays inside
 * its size budget. Both values flow up via `model()` and the dialog sends
 * them as `CreateTaskRequest.mode` / `CreateTaskRequest.allowWebAccess`.
 */
@Component({
  selector: 'app-create-mode-picker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [SegmentedControlComponent, TooltipDirective],
  templateUrl: './create-mode-picker.component.html',
  styleUrl: './create-mode-picker.component.scss',
})
export class CreateModePickerComponent {
  /** Two-way: the chosen execution mode. */
  readonly mode = model<TaskMode>('coding');
  /** Two-way: whether the agent may access the web during the task. */
  readonly allowWebAccess = model<boolean>(false);

  readonly modeOptions: readonly ModeOption[] = [
    { value: 'coding', label: 'Coding', hint: 'Default. The agent reads and writes source to implement the task.' },
    { value: 'planning', label: 'Planning', hint: 'Read-only. The agent investigates and produces a plan without writing source.' },
    { value: 'research', label: 'Research', hint: 'Read-only with web access. The agent gathers information and reports findings.' },
    { value: 'concept', label: 'Concept', hint: 'One docs-only Dossier for human review; product code stays unchanged.' },
  ];
  readonly segmentedModeOptions = this.modeOptions.map(m => ({ value: m.value, label: m.label, tooltip: m.hint, testid: 'create-mode-' + m.value }));

  /** Per-mode default for the web-access toggle (Decision 2). */
  static webDefaultFor(mode: TaskMode): boolean {
    return mode === 'research';
  }

  setMode(mode: TaskMode): void {
    if (this.mode() === mode) return;
    this.mode.set(mode);
    this.allowWebAccess.set(CreateModePickerComponent.webDefaultFor(mode));
  }

  toggleWebAccess(): void {
    this.allowWebAccess.update((v) => !v);
  }
}
