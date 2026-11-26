import { ChangeDetectionStrategy, Component, Injector, computed, inject, input } from '@angular/core';
import { type TaskInfo } from '../../models/task.model';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';

@Component({
  selector: 'app-card-waiting-line',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './card-waiting-line.html',
  styleUrl: './card-waiting-line.scss',
})
export class CardWaitingLineComponent {
  readonly task = input.required<TaskInfo>();
  readonly detail = input(false);
  private readonly injector = inject(Injector);
  readonly dependencyKeys = computed(() => this.task().waitingReason
    ? (this.task().waitsOn?.items ?? []).filter(item => !item.fulfilled).map(item => item.key) : []);

  open(key: string, event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.injector.get(TaskReferenceNavigationService).openReferenceOrNotify(key);
  }
}
