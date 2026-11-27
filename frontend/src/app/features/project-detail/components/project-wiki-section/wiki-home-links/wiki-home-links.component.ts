import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiHome, WikiHomeLink, WikiNodeType } from '../../../../../models/project-docs.model';
import { ProjectDocsService } from '../../../../../services/project-docs.service';

/** What the parent needs to open a curated entry link. */
export interface WikiHomeOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

/**
 * Curated "Einstiege" block at the very top of the wiki Pulse landing view:
 * the sections and links of `GET /wiki/home`. A link with `exists=false`
 * renders dimmed with a "Seite nicht gefunden" tooltip and does not navigate.
 * Fetches its own payload once per project; renders nothing while loading,
 * on error, or when no sections are curated, so the landing view stays clean.
 */
@Component({
  selector: 'app-wiki-home-links',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-home-links.component.html',
  styleUrl: './wiki-home-links.component.scss',
})
export class WikiHomeLinksComponent {
  readonly projectName = input.required<string>();
  readonly openLink = output<WikiHomeOpenRequest>();

  readonly home = signal<WikiHome | null>(null);

  private readonly docs = inject(ProjectDocsService);

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      this.home.set(null);
      if (!project) return;
      const subscription = this.docs.getWikiHome(project).subscribe({
        next: home => this.home.set(home),
        error: () => this.home.set(null),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  readonly sections = computed(() =>
    (this.home()?.sections ?? []).filter(section => section.links.length > 0));

  onLinkClick(link: WikiHomeLink): void {
    if (!link.exists) return;
    this.openLink.emit({ relPath: link.relPath, type: this.typeForRel(link.relPath) });
  }

  private typeForRel(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }
}
