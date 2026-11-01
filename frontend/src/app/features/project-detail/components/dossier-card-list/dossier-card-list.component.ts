import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import type { DossierCardList } from '../../../../models/project-docs.model';
import { laneName } from '../../../../models/lane-presentation';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';

@Component({
  selector: 'app-dossier-card-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DisclosureMarkerComponent],
  templateUrl: './dossier-card-list.component.html',
  styleUrl: './dossier-card-list.component.scss',
})
export class DossierCardListComponent {
  readonly list = input<DossierCardList | null>(null);
  private readonly navigation = inject(TaskReferenceNavigationService);
  readonly laneName = laneName;
  // The identity of the open Dossier; a refresh of the same Dossier keeps it equal.
  private readonly dossierIdentity = computed(() => {
    const view = this.list();
    return view ? `${view.dossierKey ?? ''}|${view.dossierPath ?? ''}` : '';
  });
  // Collapsed by default so the Dossier text comes first; reset when another Dossier opens.
  readonly expanded = linkedSignal({ source: this.dossierIdentity, computation: () => false });

  toggle(): void {
    this.expanded.update(open => !open);
  }

  open(key: string): void {
    this.navigation.openReferenceOrNotify(key);
  }
}
