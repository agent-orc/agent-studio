import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { QualityStudioService } from '../../../../services/quality-studio.service';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { ProjectDeployReleaseNotesComponent } from '../project-deploy-release-notes/project-deploy-release-notes';
import { DeployActionsComponent } from '../deploy-actions/deploy-actions';
import {
  formatAge,
  type DeployActionOption,
  type DeployActionState,
  type DeployPanel,
  type EnvironmentCardView,
  type Release,
} from '../../state/deploy-area.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Pre-delivery full-suite verdict (raw wire value) onto the shared tones; anything else stays neutral. */
const VERDICT_TONE: Readonly<Record<string, StatusTone>> = { green: 'success', red: 'danger' };
export function verdictTone(status: string): StatusTone {
  return Object.hasOwn(VERDICT_TONE, status) ? VERDICT_TONE[status] : 'neutral';
}

/** Release list with the selected release's cards, artifacts, where it runs, and where it may deploy next. */
@Component({
  selector: 'app-project-deploy-releases',
  standalone: true,
  imports: [DatePipe, EmptyStateComponent, LoadingSurfaceComponent, ProjectDeployReleaseNotesComponent, StatusIndicatorComponent,
    DeployActionsComponent],
  templateUrl: './project-deploy-releases.html',
  styleUrl: './project-deploy-releases.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleasesComponent {
  private readonly qualityStudio = inject(QualityStudioService);
  readonly projectName = input.required<string>();
  readonly panel = input.required<DeployPanel<Release[]>>();
  readonly environments = input.required<readonly EnvironmentCardView[]>();
  readonly now = input.required<number>();
  readonly total = input<number>(0);
  readonly offset = input<number>(0);
  readonly highlightedReleaseId = input<string | null>(null);
  /** Deploy options of the selected release; the parent loads them for {@link selectedRelease}. */
  readonly actions = input<DeployPanel<DeployActionOption[]>>({ status: 'loading' });
  readonly actionState = input<DeployActionState>({ busy: null, error: null });
  readonly pageChange = output<number>();
  readonly selectedRelease = output<string | null>();
  readonly startAction = output<DeployActionOption>();
  readonly selectedId = signal<string | null>(null);
  protected readonly verdictTone = verdictTone;

  constructor() {
    effect(() => this.selectedId.set(this.highlightedReleaseId()));
    effect(() => {
      const id = this.selected()?.id ?? null;
      untracked(() => this.selectedRelease.emit(id));
    });
  }

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
    this.releases().find(release => release.id === (this.selectedId() ?? this.highlightedReleaseId())) ?? this.releases()[0] ?? null);
  readonly liveIn = computed(() => {
    const release = this.selected();
    return release
      ? this.environments().filter(env => env.releaseVersion === release.version).map(env => env.name)
      : [];
  });

  readonly environmentNames = computed<ReadonlyMap<string, string>>(() =>
    new Map(this.environments().map(env => [env.id, env.name])));
  /** Options for the shown release only, so a late answer for another release never shows here. */
  readonly releaseActions = computed(() => {
    const panel = this.actions();
    const id = this.selected()?.id;
    return panel.status === 'ready' ? panel.value.filter(item => item.releaseId === id) : [];
  });

  readonly actionsError = computed(() => {
    const panel = this.actions();
    return panel.status === 'error' ? panel.message : '';
  });

  select(id: string): void { this.selectedId.set(id); }
  age(iso: string): string | null { return formatAge(iso, this.now()); }
  shortSha(sha: string): string { return sha.slice(0, 8); }
  verdictLabel(verdict: string): string {
    return verdict === 'not-reviewed' ? 'Not reviewed' : verdict[0].toUpperCase() + verdict.slice(1);
  }
  openRange(release: Release): void {
    const range = release.quality?.range;
    if (!range) return;
    if (range.requiresConfirmation && !window.confirm('Open the first release using the repository root commit as the range base?')) return;
    this.qualityStudio.openRange(this.projectName(), range.repositoryId, range.baseSha, range.headSha);
  }
}
