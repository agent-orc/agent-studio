import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { JobsHubClient } from './jobs-hub-client.service';
import { AuthSessionState } from './auth.service';

/**
 * Derives a debounced, app-wide "backend offline" signal from the SignalR hub
 * connection (the connection the browser actually holds to the backend via the
 * proxied `/hubs` route).
 *
 * Why debounce: {@link JobsHubClient.connected} flips false on every transient
 * auto-reconnect (the back-off schedule is 0s/2s/5s/10s/30s), so binding a
 * banner straight to it would flicker. We only declare the backend *offline*
 * once the socket has been down continuously for {@link GRACE_MS}. The flip
 * back to online is immediate — the moment the hub reconnects, the warning
 * clears and {@link JobsHubClient}'s `reconnected` hook re-hydrates the board.
 *
 * Cached data is intentionally NOT cleared while offline: views keep showing
 * what they last loaded (marked stale by the banner) so the operator can still
 * read already-fetched data, per the "backend down must be immediately visible,
 * but viewing loaded data is fine" requirement.
 */
@Injectable({ providedIn: 'root' })
export class ConnectionStatusService {
  private readonly hub = inject(JobsHubClient);
  private readonly auth = inject(AuthSessionState);

  /** True only after the hub has been down continuously for {@link GRACE_MS}. */
  readonly offline = signal(false);

  /** Convenience inverse for templates that read positively. */
  readonly online = computed(() => !this.offline());

  private graceHandle: ReturnType<typeof setTimeout> | null = null;

  /** How long the socket must stay down before we call it offline. */
  private static readonly GRACE_MS = 4000;

  constructor() {
    effect(() => {
      const up = this.hub.connected();
      untracked(() => {
        if (up) {
          // Reconnected (or never lost): clear any pending grace timer and the
          // banner immediately.
          if (this.graceHandle) {
            clearTimeout(this.graceHandle);
            this.graceHandle = null;
          }
          this.offline.set(false);
        } else if (this.graceHandle === null) {
          // Socket is down: start the grace window. If it is still down when
          // the timer fires, declare offline. A reconnect within the window
          // cancels it above, so transient back-off blips never show.
          this.graceHandle = setTimeout(() => {
            this.graceHandle = null;
            // AGT-3668: an account without a project has nothing to stream;
            // its refused socket is not a backend outage.
            if (!this.hub.connected() && !this.auth.withoutProject()) this.offline.set(true);
          }, ConnectionStatusService.GRACE_MS);
        }
      });
    });
  }
}
