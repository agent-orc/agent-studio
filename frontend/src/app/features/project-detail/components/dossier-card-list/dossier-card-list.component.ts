import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import type { DossierCardList } from '../../../../models/project-docs.model';
import { laneName } from '../../../../models/lane-presentation';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

@Component({
  selector: 'app-dossier-card-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dossier-card-list.component.html',
  styleUrl: './dossier-card-list.component.scss',
})
export class DossierCardListComponent {
  readonly list = input<DossierCardList | null>(null);
  private readonly navigation = inject(TaskReferenceNavigationService);
  readonly laneName = laneName;

  open(key: string): void {
    this.navigation.openReferenceOrNotify(key);
  }
}
