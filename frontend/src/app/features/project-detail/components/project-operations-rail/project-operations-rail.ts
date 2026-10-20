import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { ProjectOperationsComponent } from '../project-operations/project-operations';
import { OperationsCardLookupService } from '../../state/operations-card-lookup.service';

@Component({
  selector: 'app-project-operations-rail', standalone: true,
  imports: [ProjectOperationsComponent], templateUrl: './project-operations-rail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectOperationsRailComponent {
  private readonly cards = inject(OperationsCardLookupService);
  readonly projectName = input.required<string>();
  readonly releaseSelected = output<string>();
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  openRelease(id: string): void {
    this.releaseSelected.emit(id);
  }

  openCard(key: string): void {
    this.cards.resolve(this.projectName(), key).subscribe(card => this.openTask.emit(card));
  }
}
