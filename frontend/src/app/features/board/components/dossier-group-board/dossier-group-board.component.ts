import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { laneName } from '../../../../models/lane-presentation';
import { DossierCatalogueService } from '../../../../services/dossier-catalogue.service';
import { groupCardsByDossier } from './dossier-grouping.util';

@Component({
  selector: 'app-dossier-group-board', standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dossier-group-board.component.html',
  styleUrl: './dossier-group-board.component.scss',
})
export class DossierGroupBoardComponent {
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly jobClick = output<TaskInfo>();
  private readonly catalogue = inject(DossierCatalogueService);
  readonly groups = computed(() => groupCardsByDossier(this.tasks()));
  readonly laneName = laneName;

  constructor() { this.catalogue.ensureLoaded().subscribe(); }

  label(path: string | null, projectName: string): string {
    if (!path) return 'No Dossier';
    const matches = this.catalogue.index().byEntryPath.get(path.toLowerCase()) ?? [];
    return matches.find(item => item.projectName === projectName)?.title ?? path;
  }
}
