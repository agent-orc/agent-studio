import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { take } from 'rxjs';
import { TaskService } from '../../../../services/task.service';
import { ProjectOperationsComponent } from '../project-operations/project-operations';
import { OperationsReleaseSelectionService } from '../../state/operations-release-selection.service';

@Component({
  selector: 'app-project-operations-rail', standalone: true,
  imports: [ProjectOperationsComponent], templateUrl: './project-operations-rail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectOperationsRailComponent {
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(OperationsReleaseSelectionService);
  readonly projectName = input.required<string>();
  readonly releaseSelected = output<void>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  openRelease(id: string): void {
    this.selection.select(id);
    this.releaseSelected.emit();
  }

  openCard(key: string): void {
    const card = this.tasks.jobs().find(item => item.projectName === this.projectName()
      && (item.key === key || item.id === key));
    if (card) { this.openTask.emit({ jobId: card.id, watchPath: card.watchPath }); return; }
    this.tasks.getArchivedTasks({ project: this.projectName(), search: key, limit: 50 }).pipe(take(1))
      .subscribe(result => {
        const archived = result.items.find(item => item.key === key || item.taskKey === key || item.id === key);
        if (archived) this.openTask.emit({ jobId: archived.id, watchPath: archived.watchPath });
      });
  }
}
