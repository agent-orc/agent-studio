import type { WritableSignal } from '@angular/core';
import type { ProjectEnvironments } from '../../../models/project-overview.model';
import type { DeployPage } from '../services/project-deploy-data.service';
import { ready, type DeployPanel, type EnvironmentCardView, type EnvironmentStatus } from './deploy-area.model';

export function deploymentEnvironment(id: string, cards: readonly EnvironmentCardView[],
  statuses: DeployPanel<EnvironmentStatus[]>, environments: DeployPanel<ProjectEnvironments>, givenName?: string):
  { name: string; production: boolean } {
  const name = givenName ?? cards.find(card => card.id === id)?.name ?? id;
  return { name, production: ready(statuses)?.some(item => item.environment.id === id && item.environment.kind === 'production')
    || ready(environments)?.environments.some(item => item.declaration.id === id && item.declaration.kind === 'production')
    || name.toLowerCase() === 'production' };
}

export function deployPageItems<T>(panel: DeployPanel<DeployPage<T>>): DeployPanel<T[]> {
  return panel.status === 'ready' ? { status: 'ready', value: panel.value.items } : panel;
}

/** With keepShown, ready data stays on screen until the reload answers. */
export function retainReadyPanel<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean):
  (panel: DeployPanel<T>) => void {
  return panel => {
    if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
  };
}
