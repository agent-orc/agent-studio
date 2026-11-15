import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { TaskPreview } from '../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../status-indicator/status-indicator.component';

/**
 * AGT-3403: preview deployment status to shared status tone. Deploying,
 * updating and removing are work in progress; a queued deployment has not
 * started yet; a status the backend adds later stays neutral.
 */
export const PREVIEW_STATUS_TONES: Readonly<Record<TaskPreview['status'], StatusTone>> = {
  queued: 'neutral',
  updating: 'running',
  deploying: 'running',
  ready: 'success',
  failed: 'danger',
  removing: 'running',
};

export function previewStatusTone(status: string | null | undefined): StatusTone {
  return PREVIEW_STATUS_TONES[status as TaskPreview['status']] ?? 'neutral';
}

@Component({
  selector: 'app-task-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent],
  templateUrl: './task-preview.component.html',
  styleUrl: './task-preview.component.scss',
})
export class TaskPreviewComponent {
  readonly preview = input<TaskPreview | null | undefined>(null);
  readonly variant = input<'card' | 'detail' | 'chat'>('card');
  readonly taskKey = input('');
  readonly previewTone = previewStatusTone;
}
