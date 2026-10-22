import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { TaskPreview } from '../../models/task.model';

@Component({
  selector: 'app-task-preview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-preview.component.html',
  styleUrl: './task-preview.component.scss',
})
export class TaskPreviewComponent {
  readonly preview = input<TaskPreview | null | undefined>(null);
  readonly variant = input<'card' | 'detail' | 'chat'>('card');
  readonly taskKey = input('');
}
