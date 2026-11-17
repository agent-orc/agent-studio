import type { WritableSignal } from '@angular/core';
import type { DeployPage } from '../services/project-deploy-data.service';
import type { DeployPanel } from './deploy-area.model';

export function pageItems<T>(panel: DeployPanel<DeployPage<T>>): DeployPanel<T[]> {
  return panel.status === 'ready' ? { status: 'ready', value: panel.value.items } : panel;
}

/** With keepShown, ready data stays on screen until the reload answers. */
export function intoPanel<T>(target: WritableSignal<DeployPanel<T>>, keepShown: boolean): (panel: DeployPanel<T>) => void {
  return panel => {
    if (!keepShown || panel.status !== 'loading' || target().status !== 'ready') target.set(panel);
  };
}
