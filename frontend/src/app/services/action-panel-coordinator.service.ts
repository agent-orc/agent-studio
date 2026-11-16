import { Injectable, effect, signal, untracked } from '@angular/core';

/**
 * One open action panel per view (AGT-3875). A view that hosts several
 * expanding action forms (the Dossier decision forms and Create
 * implementation cards) provides one coordinator; a panel claims it when it
 * opens and closes itself when another panel claims it, so no panel covers
 * another panel's confirm button.
 */
@Injectable()
export class ActionPanelCoordinator {
  private readonly current = signal<string | null>(null);
  readonly active = this.current.asReadonly();

  claim(panel: string): void {
    this.current.set(panel);
  }

  /** Claims only when no other panel is open, for a form a draft restores. */
  claimIfFree(panel: string): void {
    if (this.current() === null) this.current.set(panel);
  }

  release(panel: string): void {
    if (this.current() === panel) this.current.set(null);
  }
}

/**
 * Runs `close` whenever another panel claims the coordinator. Call it in an
 * injection context, such as a component constructor or field initializer.
 */
export function closeWhenAnotherPanelOpens(
  panels: ActionPanelCoordinator | null, panel: string, close: () => void,
): void {
  if (!panels) return;
  effect(() => {
    const active = panels.active();
    if (active !== null && active !== panel) untracked(close);
  });
}
