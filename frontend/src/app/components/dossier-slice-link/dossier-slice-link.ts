import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { dossierSlicePosition } from '../../models/dossier-presentation';
import type { TaskDossier } from '../../models/task.model';
import { DossierCatalogueService } from '../../services/dossier-catalogue.service';
import { DossierReferenceNavigationService } from '../../services/dossier-reference-navigation.service';
import { resolveDossierReference } from '../../services/dossier-reference.util';

/**
 * A card's primary Dossier and its slice position, as one link:
 * `Slice 2 of 3 · Dossier AGT-W54` (AGT-3702). The link opens the Dossier view.
 * Until the catalogue resolves the path, the line names the Dossier folder as
 * plain text, because a link to an unresolved Dossier would lead nowhere.
 */
@Component({
  selector: 'app-dossier-slice-link',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dossier-slice-link.html',
  styleUrl: './dossier-slice-link.scss',
})
export class DossierSliceLinkComponent {
  readonly dossier = input.required<TaskDossier>();
  readonly projectName = input.required<string>();
  readonly testId = input('dossier-slice-link');
  private readonly catalogue = inject(DossierCatalogueService);
  private readonly navigation = inject(DossierReferenceNavigationService);

  readonly reference = computed(() =>
    resolveDossierReference(this.dossier().path, this.catalogue.index(), this.projectName()));
  readonly position = computed(() => dossierSlicePosition(this.dossier()));
  readonly label = computed(() => {
    const name = this.reference()?.key ?? folderName(this.dossier().path);
    return [this.position(), `Dossier ${name}`].filter(Boolean).join(' · ');
  });
  readonly route = computed(() => {
    const reference = this.reference();
    return reference ? this.navigation.routeFor(reference) : null;
  });
  readonly tooltip = computed(() => {
    const dossier = this.dossier();
    return [
      this.reference()?.title ?? dossier.path,
      dossier.section ? `Section: ${dossier.section}` : null,
      dossier.decision ? `Decision: ${dossier.decision}` : null,
      `Source: ${dossier.path}`,
    ].filter(Boolean).join('\n');
  });

  constructor() {
    this.catalogue.ensureLoaded().subscribe();
  }

  open(event: MouseEvent): void {
    // Board cards open their detail on click; the link opens the Dossier only.
    event.stopPropagation();
    const reference = this.reference();
    if (!reference || event.ctrlKey || event.metaKey || event.shiftKey || event.button !== 0) return;
    event.preventDefault();
    this.navigation.openDossier(reference);
  }
}

function folderName(path: string): string {
  const segments = path.replaceAll('\\', '/').split('/').filter(Boolean);
  return segments.length > 1 ? segments[segments.length - 2] : (segments[0] ?? path);
}
