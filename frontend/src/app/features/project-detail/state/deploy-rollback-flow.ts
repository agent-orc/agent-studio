import { DestroyRef, Injectable, computed, inject, signal, type Signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { LocalOwnerSessionService } from '../../../services/local-owner-session.service';
import { ProjectDeployDataService, serverMessage } from '../services/project-deploy-data.service';
import { DEPLOY_CLOCK, ready, type DeployPanel, type DeployRun, type EnvironmentStatus } from './deploy-area.model';
import { buildRollbackOffers, rollbackConfirmation, type RollbackOffer } from './rollback-offer.model';

/** What the Deploy area lends the rollback flow. */
export interface DeployRollbackHost {
  readonly projectName: Signal<string>;
  readonly statusPanel: Signal<DeployPanel<EnvironmentStatus[]>>;
  readonly selectedRun: Signal<DeployRun | null>;
  refresh(): void;
  openRun(runId: string): void;
}

/**
 * F13: the one rollback flow behind every entry point of the Deploy area
 * (environment card, run drawer, incident card, deployment tools). It names
 * current and target release and asks first, then posts the lifecycle route.
 * For Production the confirmation is the Owner or Maintainer approval, so the
 * local profile signs in before it. Provided per Deploy area.
 */
@Injectable()
export class DeployRollbackFlow {
  private readonly data = inject(ProjectDeployDataService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly localOwner = inject(LocalOwnerSessionService);
  private readonly clock = inject(DEPLOY_CLOCK);
  private readonly requests = new Subscription();
  private host: DeployRollbackHost | null = null;
  private readonly statuses = signal<Signal<DeployPanel<EnvironmentStatus[]>> | null>(null);
  private readonly run = signal<Signal<DeployRun | null> | null>(null);

  /** Every rollback target `/deploy/status` names. */
  readonly offers = computed(() => buildRollbackOffers(ready(this.statuses()?.() ?? { status: 'loading' })));
  /** The environment whose rollback is being confirmed or sent, and the last refusal. */
  readonly state = signal<{ environmentId: string | null; error: string | null }>({ environmentId: null, error: null });
  /** The drawer offers the rollback while the run's release is the one running in its environment. */
  readonly drawerOffer = computed<RollbackOffer | null>(() => {
    const run = this.run()?.() ?? null;
    const offer = run ? this.offers().find(item => item.environmentId === run.environmentId) : undefined;
    return run && offer && run.state === 'succeeded' && run.releaseId === offer.currentReleaseId ? offer : null;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.requests.unsubscribe());
  }

  attach(host: DeployRollbackHost): this {
    this.host = host;
    this.statuses.set(host.statusPanel);
    this.run.set(host.selectedRun);
    return this;
  }

  async request(environmentId: string): Promise<void> {
    const host = this.host;
    const offer = this.offers().find(item => item.environmentId === environmentId);
    if (!host || !offer || this.state().environmentId) return;
    if (offer.production && this.localOwner.missing())
      return this.localOwner.run('Production rollback needs the owner account.', () => void this.request(environmentId));
    this.state.set({ environmentId, error: null });
    if (!await this.confirmDialog.confirm(rollbackConfirmation(offer, this.clock()))) {
      this.state.set({ environmentId: null, error: null });
      return;
    }
    const project = host.projectName();
    this.requests.add(this.data.rollback(project, environmentId,
      { approve: offer.production, acceptIncidentTarget: offer.health === 'incident' }).subscribe({
      next: run => {
        this.state.set({ environmentId: null, error: null });
        if (project !== host.projectName()) return;
        host.refresh();
        if (run?.id) host.openRun(run.id);
      },
      error: (error: unknown) => {
        this.state.set({ environmentId: null,
          error: `${offer.environmentName}: ${serverMessage(error) ?? 'The rollback was not accepted.'}`
            + ' Redeploy a known release or fix forward.' });
        if (project === host.projectName()) host.refresh();
      },
    }));
  }
}
