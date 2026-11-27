import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAge, type DeployPanel } from '../../state/deploy-area.model';
import {
  instanceRunLabel,
  instanceRunTone,
  instanceStepTone,
  shortCommit,
  utcMinute,
  type InstanceDeployView,
} from '../../state/instance-deploy.model';

/**
 * The running Studio instance as the Production environment of its source
 * project (AGT-3879): the live release, its cards, the cards integrated after
 * the live commit, and the latest deploy run. Renders nothing for any other
 * project, where the instance route answers 404.
 */
@Component({
  selector: 'app-project-instance-deploy',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './project-instance-deploy.html',
  styleUrl: './project-instance-deploy.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectInstanceDeployComponent {
  readonly panel = input.required<DeployPanel<InstanceDeployView>>();
  readonly now = input.required<number>();
  readonly openCard = output<string>();

  readonly view = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready' ? panel.value : null;
  });
  readonly run = computed(() => this.view()?.latestRun ?? null);
  readonly runTone = computed(() => instanceRunTone(this.run()?.state ?? ''));
  readonly runLabel = computed(() => {
    const run = this.run();
    return run ? instanceRunLabel(run, this.view()?.stalledAfterMinutes ?? 30) : 'No deploy reported yet';
  });
  /** Component versions of the live release: server image, engine, runner package, gate worker, review host. */
  readonly components = computed(() => Object.entries(this.view()?.live?.components ?? {}));
  readonly factsPending = computed(() => !this.view()?.factsComputedAtUtc);

  readonly shortCommit = shortCommit;
  readonly utcMinute = utcMinute;
  readonly stepTone = instanceStepTone;

  /** "5 h ago", or "just now". */
  age(iso: string | null): string | null {
    const age = formatAge(iso, this.now());
    return age && age !== 'just now' ? `${age} ago` : age;
  }
}
