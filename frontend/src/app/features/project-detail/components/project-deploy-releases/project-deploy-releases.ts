import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { QualityStudioService } from '../../../../services/quality-studio.service';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { ProjectDeployReleaseNotesComponent } from '../project-deploy-release-notes/project-deploy-release-notes';
import { formatAge, type DeployPanel, type EnvironmentCardView, type IntegrationReleaseStatus, type Release, type SecretReadinessResponse } from '../../state/deploy-area.model';
import { ProjectDeployDataService, serverMessage } from '../../services/project-deploy-data.service';
import { DeploySecretGapsComponent } from '../deploy-secret-gaps/deploy-secret-gaps';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Pre-delivery full-suite verdict (raw wire value) onto the shared tones; anything else stays neutral. */
const VERDICT_TONE: Readonly<Record<string, StatusTone>> = { green: 'success', red: 'danger' };
export function verdictTone(status: string): StatusTone {
  return Object.hasOwn(VERDICT_TONE, status) ? VERDICT_TONE[status] : 'neutral';
}

/** Next-release state onto the shared tones and a short label (AGT-3891). */
const NEXT_RELEASE: Readonly<Record<IntegrationReleaseStatus['state'], { tone: StatusTone; label: string }>> = {
  unconfigured: { tone: 'neutral', label: 'No environments' },
  'promotion-train': { tone: 'info', label: 'Promotion train' },
  'waiting-for-gate': { tone: 'running', label: 'Waiting for the gate' },
  ready: { tone: 'info', label: 'Ready to release' },
  released: { tone: 'success', label: 'Tip released' },
  blocked: { tone: 'warning', label: 'Blocked' },
};

/** Release list with the selected release's cards, artifacts, and where it runs. */
@Component({
  selector: 'app-project-deploy-releases',
  standalone: true,
  imports: [DatePipe, EmptyStateComponent, LoadingSurfaceComponent, ProjectDeployReleaseNotesComponent, StatusIndicatorComponent,
    DeploySecretGapsComponent],
  templateUrl: './project-deploy-releases.html',
  styleUrl: './project-deploy-releases.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeployReleasesComponent {
  private readonly qualityStudio = inject(QualityStudioService);
  private readonly data = inject(ProjectDeployDataService);
  readonly projectName = input.required<string>();
  readonly panel = input.required<DeployPanel<Release[]>>();
  readonly environments = input.required<readonly EnvironmentCardView[]>();
  readonly now = input.required<number>();
  readonly total = input<number>(0);
  readonly offset = input<number>(0);
  readonly highlightedReleaseId = input<string | null>(null);
  readonly pageChange = output<number>();
  /** A release was created from this tab; the Deploy area reloads releases and runs. */
  readonly released = output<void>();
  /** F14: open the binding panel on an environment the selected release misses secrets in. */
  readonly openSecrets = output<string>();
  readonly selectedId = signal<string | null>(null);
  protected readonly verdictTone = verdictTone;

  /** F14: secret readiness of the selected release, keyed so a late answer for another release never shows. */
  private readonly secretPanel = signal<{ key: string; panel: DeployPanel<SecretReadinessResponse> } | null>(null);

  readonly next = signal<DeployPanel<IntegrationReleaseStatus>>({ status: 'loading' });
  readonly creating = signal(false);
  readonly createError = signal<string | null>(null);
  private nextRead = new Subscription();

  constructor() {
    effect(() => this.selectedId.set(this.highlightedReleaseId()));
    // Read again whenever the release list is read again (load, refresh, page change).
    effect(() => {
      const project = this.projectName();
      if (this.panel().status === 'loading') return;
      untracked(() => this.loadNext(project));
    });
    inject(DestroyRef).onDestroy(() => this.nextRead.unsubscribe());
    effect(onCleanup => {
      const project = this.projectName();
      const releaseId = this.selected()?.id;
      if (!releaseId) return;
      const key = `${project}\n${releaseId}`;
      const subscription = this.data.secretReadiness(project, releaseId)
        .subscribe(panel => this.secretPanel.set({ key, panel }));
      onCleanup(() => subscription.unsubscribe());
    });
  }

  /** What creates the next release; null while loading or on a server without the route. */
  readonly nextRelease = computed(() => {
    const next = this.next();
    return next.status === 'ready' ? next.value : null;
  });
  readonly nextTone = computed(() => {
    const next = this.nextRelease();
    return next ? NEXT_RELEASE[next.state] ?? { tone: 'neutral' as const, label: next.state } : null;
  });

  create(): void {
    if (this.creating()) return;
    this.creating.set(true);
    this.createError.set(null);
    this.data.createIntegrationRelease(this.projectName()).subscribe({
      next: status => {
        this.creating.set(false);
        this.next.set({ status: 'ready', value: status });
        if (status.state === 'released') this.released.emit();
      },
      error: (error: unknown) => {
        this.creating.set(false);
        this.createError.set(serverMessage(error) ?? 'The release could not be created. Try again.');
      },
    });
  }

  private loadNext(project: string): void {
    this.nextRead.unsubscribe();
    this.nextRead = this.data.integrationRelease(project).subscribe(panel => {
      // Keep the shown status while it reloads, so the tab does not flicker on refresh.
      if (panel.status !== 'loading' || this.next().status !== 'ready') this.next.set(panel);
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

  readonly secrets = computed(() => {
    const release = this.selected();
    const state = this.secretPanel();
    if (!release || state?.key !== `${this.projectName()}\n${release.id}`) return { status: 'loading' as const, environments: [], message: null };
    const panel = state.panel;
    if (panel.status !== 'ready') return { status: panel.status, environments: [], message: panel.status === 'error' ? panel.message : null };
    return { status: 'ready' as const, environments: panel.value.environments, message: null };
  });
  readonly secretGaps = computed(() => this.secrets().environments.filter(item => !item.ready));

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
