import { computed, signal, type WritableSignal } from '@angular/core';
import { Subscription } from 'rxjs';
import { serverMessage, type ProjectDeployDataService } from '../services/project-deploy-data.service';
import {
  deployActionKey,
  ready,
  type DeployActionOption,
  type DeployActionState,
  type DeployPanel,
  type Deployment,
} from './deploy-area.model';

const IDLE: DeployActionState = { busy: null, error: null };

export interface DeployActionHost {
  project(): string;
  /** The environment's display name, and whether it is Production. */
  environment(id: string): { name: string; production: boolean };
  /** Runs after the server answered, with the started run on success. */
  settled(project: string, run: Deployment | null): void;
}

/**
 * The Deploy area's deploy, approval-request and redeploy options (server
 * lifecycle decisions) for the environment cards and the shown release, and
 * the one start request that may be in flight.
 */
export class DeployActionsStore {
  /** Per environment: deploy the next release, redeploy the current one. */
  readonly cardsPanel = signal<DeployPanel<DeployActionOption[]>>({ status: 'loading' });
  /** Deploy options of the release the Releases view shows. */
  readonly releasePanel = signal<DeployPanel<DeployActionOption[]>>({ status: 'loading' });
  readonly state = signal<DeployActionState>(IDLE);
  readonly cards = computed(() => ready(this.cardsPanel()) ?? []);
  private releaseReads = new Subscription();
  private starts = new Subscription();
  private startKey: string | null = null;
  private shownReleaseId: string | null = null;

  constructor(private readonly data: ProjectDeployDataService, private readonly host: DeployActionHost) {}

  /** Nothing of the previous project may show under the new one. */
  reset(): void {
    this.dispose();
    this.state.set(IDLE);
    this.shownReleaseId = null;
    this.cardsPanel.set({ status: 'loading' });
    this.releasePanel.set({ status: 'loading' });
  }

  dispose(): void {
    this.releaseReads.unsubscribe();
    this.starts.unsubscribe();
    this.starts = new Subscription();
    this.startKey = null;
  }

  /** Reads the card and release options; `current` drops the answer of a superseded reload. */
  load(project: string, keepShown: boolean, current: () => boolean): Subscription {
    const reads = this.data.deployActions(project).subscribe(panel => {
      if (current()) keep(this.cardsPanel, keepShown, panel);
    });
    this.loadRelease(project, keepShown);
    return reads;
  }

  selectRelease(releaseId: string | null): void {
    this.shownReleaseId = releaseId;
    this.loadRelease(this.host.project(), false);
  }

  /** The server decides again; a refusal shows its reason next to the buttons. */
  start(option: DeployActionOption): void {
    if (this.startKey || !option.allowed || !this.confirmed(option)) return;
    const project = this.host.project();
    const key = deployActionKey(option);
    this.startKey = key;
    this.state.set({ busy: key, error: null });
    this.starts.add(this.data.start(project, option).subscribe({
      next: run => {
        this.startKey = null;
        this.state.set(IDLE);
        this.host.settled(project, run);
      },
      error: (error: unknown) => {
        this.startKey = null;
        this.state.set({ busy: null,
          error: { key, message: serverMessage(error) ?? 'The deploy service did not accept the request.' } });
        this.host.settled(project, null);
      },
    }));
  }

  /** A direct Production deploy or redeploy asks first; a request for approval does not. */
  private confirmed(option: DeployActionOption): boolean {
    const environment = this.host.environment(option.environmentId);
    return !environment.production || option.needsApproval || window.confirm(
      `${option.action === 'redeploy' ? 'Redeploy' : 'Deploy'} release ${option.releaseVersion} to ${environment.name}?`);
  }

  private loadRelease(project: string, keepShown: boolean): void {
    this.releaseReads.unsubscribe();
    const releaseId = this.shownReleaseId;
    if (!releaseId) {
      this.releaseReads = new Subscription();
      this.releasePanel.set({ status: 'ready', value: [] });
      return;
    }
    this.releaseReads = this.data.releaseDeployActions(project, releaseId).subscribe(panel => {
      if (this.shownReleaseId === releaseId && project === this.host.project()) keep(this.releasePanel, keepShown, panel);
    });
  }
}

/** With `keepShown`, ready options stay on screen until the reload answers. */
function keep<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean, panel: DeployPanel<T>): void {
  if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
}
