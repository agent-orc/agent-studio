import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ClientSummary } from '../models/task.model';
import { AuthService, type AuthUser } from './auth.service';

/**
 * Reads the registry of client identities (`/api/clients`) and keeps a
 * lookup signal used by the job-card owner chip and the top-bar client
 * filter dropdown. The frontend does not register identities; that flow
 * is owned by the backend bootstrap and the future settings UI.
 */
@Injectable({ providedIn: 'root' })
export class ClientService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  readonly clients = signal<ClientSummary[]>([]);
  readonly humanNames = signal<Readonly<Record<string, string>>>({});
  private humanNamesRequested = false;
  readonly loaded = signal(false);

  /** Map id -> ClientSummary for cheap lookups in the card render path. */
  readonly byId = computed(() => {
    const map = new Map<string, ClientSummary>();
    for (const c of this.clients()) map.set(c.id, c);
    return map;
  });

  refresh(): void {
    this.humanNamesRequested = false;
    this.humanNames.set({});
    this.http.get<ClientSummary[]>('/api/clients/').subscribe({
      next: list => {
        // Corrupt-file diagnostics share the registry response so operator
        // surfaces can show them, but they are not assignable task owners.
        this.clients.set((list ?? []).filter(client => !client.identityFileError));
        this.loaded.set(true);
      },
      error: () => {
        // Identity registry is best-effort: a failure here just collapses
        // the chip to a neutral default. Don't surface an error dialog.
        this.loaded.set(true);
      }
    });
  }

  /** Resolve human task owners only when a card needs one; the owner may read the user directory. */
  loadHumanNames(): void {
    if (this.humanNamesRequested || this.auth.status()?.profile !== 'networked' || this.auth.status()?.user?.role !== 'owner') return;
    this.humanNamesRequested = true;
    this.http.get<AuthUser[]>('/api/auth/users').subscribe({
      next: users => {
        if (this.auth.status()?.user?.role === 'owner')
          this.humanNames.set(Object.fromEntries(users.map(user => [user.id, user.displayName])));
      },
      error: () => { this.humanNamesRequested = false; },
    });
  }

  /**
   * Lookup helper safe to call from templates. Falls back to a synthetic
   * placeholder summary so the chip can still render an id when the
   * identity has not yet loaded (or the job points at an unknown id).
   */
  resolve(clientId: string | null | undefined): ClientSummary {
    if (!clientId) return { id: 'unknown', displayName: 'unknown', emoji: '·', colour: null, kind: 'service', registeredAt: '', lastSeenAt: null, tokenBudgetMonthly: null, notes: null, defaultCliType: null, defaultModel: null };
    const found = this.byId().get(clientId);
    const signedIn = this.auth.status()?.user;
    const humanName = this.humanNames()[clientId] ?? (signedIn?.id === clientId ? signedIn.displayName : null);
    if (humanName) return {
      ...(found ?? { id: clientId, emoji: '·', colour: null, registeredAt: '', lastSeenAt: null,
        tokenBudgetMonthly: null, notes: null, defaultCliType: null, defaultModel: null }),
      displayName: humanName, kind: 'human',
    };
    if (found) return found;
    return { id: clientId, displayName: clientId, emoji: '·', colour: null, kind: 'service', registeredAt: '', lastSeenAt: null, tokenBudgetMonthly: null, notes: null, defaultCliType: null, defaultModel: null };
  }
}
