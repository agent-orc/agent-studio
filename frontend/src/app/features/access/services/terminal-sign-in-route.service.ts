import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { terminalSignInFromHash } from '../models/terminal-sign-in.model';

/**
 * Whether the confirmation page `#/terminal-sign-in` is open, and the code its
 * link carried. The hash is read when the app starts, before sign-in, so the
 * page opens after the Owner signs in even if the shell rewrites the hash.
 */
@Injectable({ providedIn: 'root' })
export class TerminalSignInRouteService {
  readonly request = signal<{ code: string | null } | null>(terminalSignInFromHash(window.location.hash));

  constructor() {
    const follow = () => {
      const next = terminalSignInFromHash(window.location.hash);
      if (next) this.request.set(next);
    };
    window.addEventListener('hashchange', follow);
    inject(DestroyRef).onDestroy(() => window.removeEventListener('hashchange', follow));
  }

  /** Leaves the page for the board. */
  close(): void {
    this.request.set(null);
    if (terminalSignInFromHash(window.location.hash)) window.location.hash = '#/board';
  }
}
