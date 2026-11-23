import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, combineLatest, filter, forkJoin, map, of, startWith, switchMap, take } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type {
  DeployActionOption,
  DeployPanel,
  DeployRunAction,
  Deployment,
  DeploymentLogLine,
  DeploymentLogResponse,
  EnvironmentStatus,
  IntegrationReleaseStatus,
  Release,
  DeployRunFilter,
  ReleaseGateStatus,
  SecretReadinessResponse,
} from '../state/deploy-area.model';
import { artifactReleaseLookups, buildEnvironmentCards, ready, type DeploySources, type EnvironmentCardView } from '../state/deploy-area.model';
import { ProjectPipelineDefinitionService, type PipelineDefinitionResponse } from './project-pipeline-definition.service';
import { EnvironmentHealthService } from '../../environment-health/services/environment-health.service';
import type { EnvironmentHealthResponse } from '../../environment-health/models/environment-health.model';

/** Declared environments with their state from the one environment projection. */
export interface EnvironmentStates {
  /** The AGT-3176 declarations, history and promotions; null when only `/deploy/status` answered. */
  environments: ProjectEnvironments | null;
  /** One state per environment, in the order {@link buildEnvironmentCards} returns them. */
  states: EnvironmentCardView[];
}

export interface DeployPage<T> { items: T[]; offset: number; limit: number; total: number }
export interface ReleaseNotes {
  releaseId: string;
  version: string;
  preview: boolean;
  /** `maintenance` marks chore cards; `edited` says a person wrote the text (F18). */
  notes: { cardKey: string; text: string; maintenance?: boolean; edited?: boolean }[];
}

/**
 * A 404 without an `error` body: the route is not on this server. AGT-3201 and
 * AGT-3221 answer an unknown project or deployment with `{ error }`, which
 * stays an error.
 */
export function isMissingRoute(error: unknown): boolean {
  if (!(error instanceof HttpErrorResponse) || error.status !== 404) return false;
  const body: unknown = error.error;
  return !(body && typeof body === 'object' && typeof (body as { error?: unknown }).error === 'string');
}

/** The server's `{ error }` message, if it sent one. */
export function serverMessage(error: unknown): string | null {
  const body: unknown = error instanceof HttpErrorResponse ? error.error : null;
  const message = body && typeof body === 'object' ? (body as { error?: unknown }).error : null;
  return typeof message === 'string' && message.trim() ? message : null;
}

/**
 * Reads for the project Deploy area. Every panel loads on its own, so one
 * missing or failing source leaves the others intact. Nothing here invents
 * data: a route that is not on this server reports `unavailable`, and every
 * other failure reports `error`.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDeployDataService {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  private readonly definitions = inject(ProjectPipelineDefinitionService);
  private readonly healthService = inject(EnvironmentHealthService);

  /** Declared environments, host history, and promotions (AGT-3176). */
  environments(projectName: string): Observable<DeployPanel<ProjectEnvironments>> {
    return panel(this.tasks.getProjectEnvironments(projectName), 'Environments could not be loaded.', false);
  }

  /** One AGT-3201 status per environment: current and newer release, latest run, health. */
  statuses(projectName: string): Observable<DeployPanel<EnvironmentStatus[]>> {
    return panel(this.http.get<EnvironmentStatus[]>(`${deployRoot(projectName)}/status`),
      'Environment status could not be loaded.', true);
  }

  releases(projectName: string): Observable<DeployPanel<Release[]>> {
    return panel(this.http.get<Release[]>(`${deployRoot(projectName)}/releases`), 'Releases could not be loaded.', true);
  }

  releaseNotes(projectName: string, releaseId: string): Observable<DeployPanel<ReleaseNotes>> {
    return panel(this.http.get<ReleaseNotes>(`${deployRoot(projectName)}/releases/${encodeURIComponent(releaseId)}/notes`),
      'Release notes could not be loaded.', true);
  }

  /** Rewrites one card's note in product language; empty text restores the card title. */
  editReleaseNote(projectName: string, releaseId: string, cardKey: string, text: string): Observable<ReleaseNotes> {
    return this.http.put<ReleaseNotes>(
      `${deployRoot(projectName)}/releases/${encodeURIComponent(releaseId)}/notes/${encodeURIComponent(cardKey)}`, { text });
  }

  releasePage(projectName: string, offset: number): Observable<DeployPanel<DeployPage<Release>>> {
    return panel(this.http.get<DeployPage<Release>>(`${deployRoot(projectName)}/releases/page`,
      { params: { offset, limit: 25 } }), 'Releases could not be loaded.', true);
  }

  releasePageContaining(projectName: string, releaseId: string): Observable<DeployPanel<DeployPage<Release>>> {
    return panel(this.http.get<DeployPage<Release>>(
      `${deployRoot(projectName)}/releases/${encodeURIComponent(releaseId)}/page`, { params: { limit: 25 } }),
    'The selected release could not be loaded.', true);
  }

  /**
   * The newest release that carries the artifact's digest and existed at `notAfterUtc`,
   * one bounded read instead of scanning the release history for a host artifact.
   */
  releaseForArtifact(projectName: string, artifact: string, notAfterUtc: string | null): Observable<DeployPanel<Release | null>> {
    const params: Record<string, string | number> = { offset: 0, limit: 1, artifact };
    if (notAfterUtc) params['notAfterUtc'] = notAfterUtc;
    const request = this.http.get<DeployPage<Release>>(`${deployRoot(projectName)}/releases/page`, { params })
      .pipe(map(page => page.items[0] ?? null));
    return panel(request, 'The release of the deployed artifact could not be loaded.', true);
  }

  /** What creates the next release and what blocks it (AGT-3891). */
  integrationRelease(projectName: string): Observable<DeployPanel<IntegrationReleaseStatus>> {
    return panel(this.http.get<IntegrationReleaseStatus>(`${deployRoot(projectName)}/integration-release`),
      'The next release could not be determined.', true);
  }

  /** Creates the release of the gated integration branch tip; the answer is the new status. */
  createIntegrationRelease(projectName: string): Observable<IntegrationReleaseStatus> {
    return this.http.post<IntegrationReleaseStatus>(`${deployRoot(projectName)}/integration-release`, {});
  }

  /** F2: the standing release-candidate gate refusal, if the last release attempt was refused. */
  releaseGate(projectName: string): Observable<DeployPanel<ReleaseGateStatus>> {
    return panel(this.http.get<ReleaseGateStatus>(`${deployRoot(projectName)}/release-gate`),
      'The release gate state could not be loaded.', true);
  }

  /**
   * F14: secrets a deploy would miss per environment, for the release or, without
   * one, for the newest release the next run deploys.
   */
  secretReadiness(projectName: string, releaseId?: string | null): Observable<DeployPanel<SecretReadinessResponse>> {
    const options = releaseId ? { params: { releaseId } } : {};
    return panel(this.http.get<SecretReadinessResponse>(`${deployRoot(projectName)}/secret-readiness`, options),
      'Secret bindings could not be checked.', true);
  }

  /** AGT-3201 deploy runs, newest first. */
  deployments(projectName: string): Observable<DeployPanel<Deployment[]>> {
    return panel(this.http.get<Deployment[]>(`${deployRoot(projectName)}/deployments`),
      'Deploy runs could not be loaded.', true);
  }

  deploymentPage(projectName: string, offset: number, filter?: DeployRunFilter, now?: number): Observable<DeployPanel<DeployPage<Deployment>>> {
    const params: Record<string, string | number> = { offset, limit: 25 };
    if (filter?.environmentId && filter.environmentId !== 'all') params['environmentId'] = filter.environmentId;
    if (filter?.state && filter.state !== 'all') params['state'] = filter.state;
    if (filter?.period && filter.period !== 'all' && now !== undefined) {
      const days = filter.period === '24h' ? 1 : filter.period === '7d' ? 7 : 30;
      params['sinceUtc'] = new Date(now - days * 86_400_000).toISOString();
    }
    return panel(this.http.get<DeployPage<Deployment>>(`${deployRoot(projectName)}/deployments/page`, { params }),
      'Deploy runs could not be loaded.', true);
  }

  /** One run by id, for a run that no loaded page contains. */
  deployment(projectName: string, deploymentId: string): Observable<DeployPanel<Deployment>> {
    return panel(this.http.get<Deployment>(`${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}`),
      'The deploy run could not be loaded.', true);
  }

  deploymentLog(projectName: string, deploymentId: string): Observable<DeployPanel<DeploymentLogLine[]>> {
    const request = this.http
      .get<DeploymentLogResponse>(`${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/log`)
      .pipe(map(body => body.lines));
    return panel(request, 'The step log could not be loaded.', true);
  }

  /**
   * Approve, reject, or cancel a run; the server checks role and state. With
   * `confirmMissingSecrets` a Production approval goes ahead without the secret
   * bindings the server would otherwise refuse it for (F14).
   */
  act(projectName: string, deploymentId: string, action: DeployRunAction, reason?: string,
    options: { confirmMissingSecrets?: boolean } = {}): Observable<Deployment> {
    const intervention = action === 'retry' || action === 'skip' || action === 'abort';
    return this.http.post<Deployment>(
      `${deployRoot(projectName)}/deployments/${encodeURIComponent(deploymentId)}/${intervention ? 'intervention' : action}`,
      intervention ? { action, reason }
        : action === 'approve' && options.confirmMissingSecrets ? { confirmMissingSecrets: true } : {});
  }

  /** Per environment: deploy the next release and redeploy the current one, as the lifecycle allows. */
  deployActions(projectName: string): Observable<DeployPanel<DeployActionOption[]>> {
    return panel(this.http.get<DeployActionOption[]>(`${deployRoot(projectName)}/actions`),
      'Deploy actions could not be loaded.', true);
  }

  releaseDeployActions(projectName: string, releaseId: string): Observable<DeployPanel<DeployActionOption[]>> {
    return panel(this.http.get<DeployActionOption[]>(
      `${deployRoot(projectName)}/releases/${encodeURIComponent(releaseId)}/actions`),
    'Deploy actions could not be loaded.', true);
  }

  /** Starts the option through the lifecycle routes; the server decides again and names a refusal. */
  start(projectName: string, option: DeployActionOption): Observable<Deployment> {
    const approve = !option.needsApproval;
    return option.action === 'redeploy'
      ? this.http.post<Deployment>(
        `${deployRoot(projectName)}/environments/${encodeURIComponent(option.environmentId)}/redeploy`, { approve })
      : this.http.post<Deployment>(
        `${deployRoot(projectName)}/releases/${encodeURIComponent(option.releaseId)}/deployments`,
        { environmentId: option.environmentId, approve });
  }

  /**
   * The lifecycle rollback route (F13). With `approve` the request is the
   * approval: the server queues it for an Owner or Maintainer and refuses it
   * for anyone else. `acceptIncidentTarget` confirms a target that had an
   * incident while running.
   */
  rollback(projectName: string, environmentId: string,
    body: { approve: boolean; acceptIncidentTarget: boolean }): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${deployRoot(projectName)}/environments/${encodeURIComponent(environmentId)}/rollback`, body);
  }

  stopEnvironment(projectName: string, environmentId: string): Observable<void> {
    return this.http.post<void>(`/api/projects/${encodeURIComponent(projectName)}/environments/${encodeURIComponent(environmentId)}/stop`, {});
  }

  confirmRunTargetRemoval(projectName: string, environmentId: string, hostId: string): Observable<void> {
    return this.http.post<void>(`/api/projects/${encodeURIComponent(projectName)}/environments/${encodeURIComponent(environmentId)}/run-target-removal/confirm`, { hostId });
  }

  pipeline(projectName: string): Observable<DeployPanel<PipelineDefinitionResponse>> {
    return panel(this.definitions.read(projectName), 'The project.yml pipeline could not be loaded.', true);
  }

  health(projectName: string): Observable<DeployPanel<EnvironmentHealthResponse>> {
    return panel(this.healthService.project(projectName), 'Environment health could not be loaded.', true);
  }

  /**
   * The environment state projection ({@link buildEnvironmentCards}) for a
   * surface outside the Deploy overview: declarations, `/deploy/status`,
   * control-plane health with its open incident, and the release of each host
   * artifact. A failing health or status source degrades to the next one.
   */
  environmentStates(projectName: string): Observable<DeployPanel<EnvironmentStates>> {
    const settled = <T>(source: Observable<DeployPanel<T>>) => source.pipe(filter(item => item.status !== 'loading'), take(1));
    return combineLatest([
      settled(this.environments(projectName)), settled(this.statuses(projectName)), settled(this.health(projectName)),
    ]).pipe(
      switchMap(([environments, statuses, health]): Observable<DeployPanel<EnvironmentStates>> => {
        if (environments.status === 'error' && statuses.status !== 'ready') return of({ status: 'error', message: environments.message });
        const sources: DeploySources = {
          environments: ready(environments), statuses: ready(statuses), deployments: null, releases: null, health: ready(health),
        };
        // Without AGT-3201 there are no release records to look up.
        const lookups = statuses.status === 'ready' ? artifactReleaseLookups(sources) : [];
        const releases = lookups.length === 0 ? of([] as [string, DeployPanel<Release | null>][])
          : forkJoin(lookups.map(lookup => settled(this.releaseForArtifact(projectName, lookup.artifact, lookup.notAfterUtc))
            .pipe(map(result => [lookup.key, result] as [string, DeployPanel<Release | null>]))));
        return releases.pipe(map((entries): DeployPanel<EnvironmentStates> => ({
          status: 'ready',
          value: { environments: sources.environments, states: buildEnvironmentCards({ ...sources, artifactReleases: new Map(entries) }) },
        })));
      }),
      startWith<DeployPanel<EnvironmentStates>>({ status: 'loading' }),
    );
  }
}

function deployRoot(projectName: string): string {
  return `/api/projects/${encodeURIComponent(projectName)}/deploy`;
}

function panel<T>(source: Observable<T>, message: string, missingRouteIsUnavailable: boolean): Observable<DeployPanel<T>> {
  return source.pipe(
    map(value => ({ status: 'ready', value }) as DeployPanel<T>),
    catchError((error: unknown) => of<DeployPanel<T>>(
      missingRouteIsUnavailable && isMissingRoute(error)
        ? { status: 'unavailable' }
        : { status: 'error', message: serverMessage(error) ?? message })),
    startWith<DeployPanel<T>>({ status: 'loading' }),
  );
}
