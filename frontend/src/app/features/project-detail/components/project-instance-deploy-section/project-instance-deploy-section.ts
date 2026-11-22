import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProjectInstanceDeployComponent } from '../project-instance-deploy/project-instance-deploy';
import { InstanceDeployDataService } from '../../services/instance-deploy-data.service';
import type { DeployPanel } from '../../state/deploy-area.model';
import { intoPanel } from '../../state/deploy-area-reads';
import type { InstanceDeployView } from '../../state/instance-deploy.model';

/**
 * Loads the running Studio instance for the Bereitstellung overview (AGT-3879)
 * and owns its read, so the deploy area only places this tag. It re-reads when
 * the area's clock advances (every load and live refresh) and keeps the shown
 * view while a refresh is in flight. A failed read names itself here, because
 * the instance panel renders nothing outside its source project.
 */
@Component({
  selector: 'app-project-instance-deploy-section',
  standalone: true,
  imports: [ProjectInstanceDeployComponent],
  templateUrl: './project-instance-deploy-section.html',
  styleUrl: './project-instance-deploy-section.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectInstanceDeploySectionComponent {
  private readonly data = inject(InstanceDeployDataService);
  private read = new Subscription();
  private shownProject: string | null = null;

  readonly project = input.required<string>();
  readonly now = input.required<number>();
  readonly openCard = output<string>();
  readonly panel = signal<DeployPanel<InstanceDeployView>>({ status: 'loading' });

  constructor() {
    effect(() => {
      const project = this.project();
      this.now();
      untracked(() => this.load(project));
    });
    inject(DestroyRef).onDestroy(() => this.read.unsubscribe());
  }

  private load(project: string): void {
    const keepShown = project === this.shownProject;
    if (!keepShown) this.panel.set({ status: 'loading' });
    this.shownProject = project;
    this.read.unsubscribe();
    this.read = this.data.instance(project).subscribe(intoPanel(this.panel, keepShown));
  }
}
