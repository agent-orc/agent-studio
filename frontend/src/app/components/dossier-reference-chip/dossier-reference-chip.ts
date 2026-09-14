import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { StudioIconComponent } from '../studio-icon/studio-icon.component';
import { dossierPatternIcon, dossierStatusLabel } from '../../models/dossier-presentation';
import { DossierReferenceNavigationService } from '../../services/dossier-reference-navigation.service';
import type { DossierReference } from '../../services/dossier-reference.util';

/**
 * The Dossier's chip, sibling of the task reference microcard (AGT-2812).
 *
 * One quiet line: pattern glyph, reference key, title, and the status when it
 * is known. The primary action is the Dossier view - never the raw file - and
 * the entry-point source stays available as the secondary action.
 */
@Component({
  selector: 'app-dossier-reference-chip',
  standalone: true,
  imports: [NgTemplateOutlet, AppTooltipDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dossier-reference-chip.html',
  styleUrl: './dossier-reference-chip.scss',
})
export class DossierReferenceChipComponent {
  readonly reference = input.required<DossierReference>();
  /**
   * Rows that already navigate to the Dossier (search results) embed the chip
   * as presentation only; a link inside their button would be invalid markup.
   */
  readonly interactive = input(true);
  /** Drop the title where the host row already shows it (search results). */
  readonly compact = input(false);
  readonly testId = input('dossier-reference-chip');
  private readonly navigation = inject(DossierReferenceNavigationService);

  readonly label = computed(() => this.reference().key ?? this.reference().id);
  readonly icon = computed(() => dossierPatternIcon(this.reference().pattern));
  readonly statusLabel = computed(() => dossierStatusLabel(this.reference()));
  readonly route = computed(() => this.navigation.routeFor(this.reference()));
  readonly ariaLabel = computed(() =>
    `Open Dossier ${this.label()}: ${this.reference().title}`);
  readonly sourceLabel = computed(() => `Open source ${this.reference().entryPath}`);
  readonly tooltipLabel = computed(() => [
    `Dossier: ${this.label()}`,
    `Title: ${this.reference().title}`,
    `Status: ${this.statusLabel()}`,
    `Project: ${this.reference().projectName}`,
    `Source: ${this.reference().entryPath}`,
  ].join('\n'));

  open(event: MouseEvent): void {
    event.preventDefault();
    this.navigation.openDossier(this.reference());
  }

  openSource(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.navigation.openSource(this.reference());
  }
}
