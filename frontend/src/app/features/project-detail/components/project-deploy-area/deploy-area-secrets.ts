import { DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import type { ProjectDeployDataService } from '../../services/project-deploy-data.service';
import { ready, type DeployPanel, type EnvironmentSecretReadiness, type SecretReadinessResponse } from '../../state/deploy-area.model';

/** What the helper reads from the Deploy view that owns it. */
export interface DeployAreaSecretsHost {
  projectName(): string;
  selectedRunId(): string | null;
  selectedRun(): { id: string; releaseId: string; environmentId: string } | null;
  view(): string;
}

/**
 * F14: the secret readiness the Deploy view shows before a run is queued: per
 * environment for the newest release (environment cards) and for the release
 * of the selected run (drawer). Answers for another project or run are dropped.
 * Create it in an injection context: it follows the selected run and reads
 * again when the operator leaves the secrets view, where bindings change.
 */
export class DeployAreaSecrets {
  /** Secrets the next run misses per environment, from the newest release. */
  readonly panel = signal<DeployPanel<SecretReadinessResponse>>({ status: 'loading' });
  /** The environment the binding panel opens on after a Bind action. */
  readonly environment = signal<string | null>(null);
  /** Keyed by run so another run's answer never shows. */
  private readonly runPanel = signal<{ runId: string; panel: DeployPanel<SecretReadinessResponse> } | null>(null);
  private reads = new Subscription();
  private runReads = new Subscription();

  constructor(private readonly data: ProjectDeployDataService, private readonly host: DeployAreaSecretsHost) {
    inject(DestroyRef).onDestroy(() => this.stop());
    const runKey = computed(() => {
      const runId = host.selectedRunId();
      const releaseId = host.selectedRun()?.releaseId;
      return runId && releaseId ? `${runId}\n${releaseId}` : null;
    });
    effect(() => {
      const key = runKey();
      untracked(() => key ? this.loadRun(key.split('\n')[0]) : this.clearRun());
    });
    let previous = untracked(() => host.view());
    effect(() => {
      const current = host.view();
      if (previous === 'secrets' && current !== 'secrets') untracked(() => this.reload());
      previous = current;
    });
  }

  /** Secrets the selected run's release misses in its environment; null while unknown. */
  readonly run = computed<EnvironmentSecretReadiness | null>(() => {
    const run = this.host.selectedRun();
    const state = this.runPanel();
    const value = run && state?.runId === run.id ? ready(state.panel) : null;
    return value?.environments.find(item => item.environmentId === run!.environmentId) ?? null;
  });

  forEnvironment(environmentId: string): EnvironmentSecretReadiness | null {
    return ready(this.panel())?.environments.find(item => item.environmentId === environmentId) ?? null;
  }

  /** The names a Production approval of the selected run would go ahead without; empty for other actions. */
  approvalGap(action: string): string[] {
    const run = this.run();
    return action === 'approve' && run?.requiresApproval ? run.missing.map(item => item.name) : [];
  }


  /** Reads again for the project and the selected run, after bindings may have changed. */
  reload(): void {
    this.load(this.host.projectName());
    const runId = this.host.selectedRunId();
    if (runId) this.loadRun(runId);
  }

  /** With `reset`, a new project starts without readiness or a Bind target. */
  load(project: string, reset = false): void {
    if (reset) {
      this.environment.set(null);
      this.clearRun();
      this.panel.set({ status: 'loading' });
    }
    this.reads.unsubscribe();
    this.reads = this.data.secretReadiness(project).subscribe(panel => {
      if (project !== this.host.projectName()) return;
      if (panel.status !== 'loading' || this.panel().status !== 'ready') this.panel.set(panel);
    });
  }

  loadRun(runId: string): void {
    const project = this.host.projectName();
    const releaseId = this.host.selectedRun()?.releaseId;
    this.runReads.unsubscribe();
    if (!releaseId) {
      this.runPanel.set(null);
      return;
    }
    this.runReads = this.data.secretReadiness(project, releaseId).subscribe(panel => {
      if (this.host.selectedRunId() !== runId || project !== this.host.projectName()) return;
      if (panel.status !== 'loading' || this.runPanel()?.runId !== runId) this.runPanel.set({ runId, panel });
    });
  }

  clearRun(): void {
    this.runReads.unsubscribe();
    this.runPanel.set(null);
  }

  stop(): void {
    this.reads.unsubscribe();
    this.runReads.unsubscribe();
  }
}

/** The confirmation addition naming the secret bindings an approval goes ahead without. */
export function missingSecretsPrompt(names: readonly string[]): string {
  return names.length
    ? ` ${names.length} secret ${names.length === 1 ? 'binding is' : 'bindings are'} missing: ${names.join(', ')}. The deploy host will refuse the job until they are bound.`
    : '';
}
