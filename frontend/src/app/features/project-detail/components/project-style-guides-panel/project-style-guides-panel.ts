import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ProjectStyleGuide, ProjectStyleGuideCatalogue } from '../../../../models/project-docs.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';

/**
 * Compact Wiki Pulse projection of the repository style-guide catalogue.
 * Selection stays a parent-owned navigation action so this component carries
 * no second document-loading path.
 */
@Component({
  selector: 'app-project-style-guides-panel',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-style-guides-panel.html',
  styleUrl: './project-style-guides-panel.scss',
})
export class ProjectStyleGuidesPanelComponent {
  readonly projectName = input.required<string>();
  readonly openGuide = output<string>();
  readonly catalogue = signal<ProjectStyleGuideCatalogue | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  private readonly docs = inject(ProjectDocsService);

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      this.catalogue.set(null);
      this.error.set(null);
      this.loading.set(true);
      if (!project) {
        this.loading.set(false);
        return;
      }
      const subscription = this.docs.getProjectStyleGuides(project).subscribe({
        next: catalogue => {
          this.catalogue.set(catalogue);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Engineering style guides could not be loaded.');
          this.loading.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  projectMatchLabel(catalogue: ProjectStyleGuideCatalogue, guide: ProjectStyleGuide): string {
    return guide.match.projectWildcard
      ? 'Matches all projects'
      : `Project ${guide.match.projectSelector} matches ${catalogue.projectKey}`;
  }

  technologyMatchLabel(guide: ProjectStyleGuide): string {
    if (guide.match.technologyWildcard) return 'Matches any technology';
    return `Technology: ${guide.match.technologies.map(technology => technology.displayLabel).join(', ')}`;
  }
}
