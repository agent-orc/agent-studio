import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { formatAge, type DeployPanel, type EnvironmentCardView, type Release } from '../../state/deploy-area.model';

/** Release list with the selected release's cards, artifacts, and where it runs. */
@Component({
  selector: 'app-project-deploy-releases',
  standalone: true,
  imports: [DatePipe, EmptyStateComponent, LoadingSurfaceComponent],
  templateUrl: './project-deploy-releases.html',
  styleUrl: './project-deploy-releases.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleasesComponent {
  readonly panel = input.required<DeployPanel<Release[]>>();
  readonly environments = input.required<readonly EnvironmentCardView[]>();
  readonly now = input.required<number>();
  readonly selectedId = signal<string | null>(null);

  readonly releases = computed(() => {
    const panel = this.panel();
    return panel.status === 'ready'
      ? [...panel.value].sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc))
      : [];
  });
  readonly errorMessage = computed(() => {
    const panel = this.panel();
    return panel.status === 'error' ? `${panel.message} Environments and history are unaffected; try Refresh.` : '';
  });
  readonly selected = computed<Release | null>(() =>
    this.releases().find(release => release.id === this.selectedId()) ?? this.releases()[0] ?? null);
  readonly liveIn = computed(() => {
    const release = this.selected();
    return release
      ? this.environments().filter(env => env.releaseVersion === release.version).map(env => env.name)
      : [];
  });

  select(id: string): void { this.selectedId.set(id); }
  age(iso: string): string | null { return formatAge(iso, this.now()); }
  shortSha(sha: string): string { return sha.slice(0, 8); }
}
