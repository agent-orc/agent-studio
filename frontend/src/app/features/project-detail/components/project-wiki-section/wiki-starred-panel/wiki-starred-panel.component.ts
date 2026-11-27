import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiNodeType } from '../../../../../models/project-docs.model';
import { WikiStarEntry, WikiStarsService } from '../wiki-stars.service';

/** What the parent needs to open a starred entry in the reader. */
export interface WikiStarredOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

/**
 * "Gestarrt" block of the wiki landing view, sitting directly above the
 * curated "Einstiege" block: the operator's starred documents (label captured
 * at starring time + dimmed relPath), most recently starred first. Clicking an
 * entry emits open intent (the parent owns the reader flow); the star button
 * on each entry unstars it in place via {@link WikiStarsService}. The parent
 * guards mounting behind a non-empty star list so an empty block leaves no
 * host element in the landing DOM.
 */
@Component({
  selector: 'app-wiki-starred-panel',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-starred-panel.component.html',
  styleUrl: './wiki-starred-panel.component.scss',
})
export class WikiStarredPanelComponent {
  readonly projectName = input.required<string>();
  readonly openEntry = output<WikiStarredOpenRequest>();

  private readonly stars = inject(WikiStarsService);

  /** Live starred documents of the project, most recently starred first. */
  readonly entries = computed<readonly WikiStarEntry[]>(() =>
    this.stars.entries(this.projectName()));

  onEntryClick(entry: WikiStarEntry): void {
    this.openEntry.emit({ relPath: entry.relPath, type: this.typeForRel(entry.relPath) });
  }

  unstar(event: Event, entry: WikiStarEntry): void {
    event.stopPropagation();
    this.stars.unstar(this.projectName(), entry.relPath);
  }

  private typeForRel(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }
}
