import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, output } from '@angular/core';
import { Subscription } from 'rxjs';
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
  private cardRead = new Subscription();

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cardRead.unsubscribe());
  }

  openRelease(id: string): void {
    this.releaseSelected.emit(id);
  }

  /** A later card click replaces a lookup that has not answered yet. */
  openCard(key: string): void {
    this.cardRead.unsubscribe();
    this.cardRead = this.cards.resolve(this.projectName(), key).subscribe(card => this.openTask.emit(card));
  }
}
