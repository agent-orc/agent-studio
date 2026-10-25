import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { ProjectDeployDataService, type ReleaseNotes } from '../../services/project-deploy-data.service';
import type { DeployPanel, Release } from '../../state/deploy-area.model';

/** Product-language notes generated from the selected release's included cards. */
@Component({
  selector: 'app-project-deploy-release-notes',
  standalone: true,
  templateUrl: './project-deploy-release-notes.html',
  styleUrl: './project-deploy-release-notes.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleaseNotesComponent {
  private readonly data = inject(ProjectDeployDataService);
  readonly release = input.required<Release>();
  readonly panel = signal<DeployPanel<ReleaseNotes>>({ status: 'loading' });
  readonly notes = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready' ? panel.value : null;
  });

  constructor() {
    effect(onCleanup => {
      const release = this.release();
      const subscription = this.data.releaseNotes(release.projectId, release.id)
        .subscribe(value => this.panel.set(value));
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
