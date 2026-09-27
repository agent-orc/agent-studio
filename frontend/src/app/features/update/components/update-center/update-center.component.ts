import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  inject,
  signal,
} from '@angular/core';

import { UpdateClientService } from '../../../../services/update.service';
import { DevToolsService } from '../../../../services/dev-tools.service';
import { ErrorDialogService } from '../../../../services/error-dialog.service';
import { UpdateHistoryEntry } from '../../../../models/update-service.model';

import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Drawer-style overlay opened from the version badge. Three sections:
 *
 *   1. Identity card — version + commit SHA + behindBy summary.
 *   2. Pending commits list — what's queued on origin/main.
 *   3. Recent runs — last N successful/failed updates from history.
 *
 * In dev mode the bottom of the drawer offers a manual "Update now" trigger.
 * Outside dev mode, the drawer is read-only — pure release-notes feel.
 */
@Component({
  selector: 'app-update-center',
  standalone: true,
  imports: [TooltipDirective, OverlayPortalDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './update-center.component.html',
  styleUrl: './update-center.component.scss',
})
export class UpdateCenterComponent implements OnDestroy {
  private readonly client = inject(UpdateClientService);
  private readonly devTools = inject(DevToolsService);
  private readonly errors = inject(ErrorDialogService);

  readonly open = this.client.centerOpen;
  readonly status = this.client.status;
  readonly isRunning = this.client.isRunning;
  readonly isDev = computed(() => this.devTools.flags().updateStableEnabled);
  readonly serviceUnreachable = this.client.serviceUnreachable;
  readonly triggerInFlight = signal(false);
  readonly refreshInFlight = signal(false);
  readonly history = signal<UpdateHistoryEntry[]>([]);
  readonly canTrigger = computed(
    () =>
      this.isDev() && !this.serviceUnreachable() && !this.triggerInFlight() && !this.isRunning(),
  );

  private readonly historyTimer: ReturnType<typeof setInterval>;

  constructor() {
    // Re-load history every time the drawer opens.
    this.historyTimer = setInterval(() => {
      if (this.open()) this.refreshHistory();
    }, 5_000);
  }

  ngOnDestroy(): void {
    clearInterval(this.historyTimer);
  }

  close(): void {
    this.client.closeCenter();
  }

  shortSha(value: string | null | undefined): string {
    return value?.slice(0, 8) || 'unknown';
  }

  formatDate(value: string | null | undefined): string {
    if (!value) return 'Time unknown';
    return new Intl.DateTimeFormat('en-US', {
      dateStyle: 'medium', timeStyle: 'short',
    }).format(new Date(value));
  }

  deltaLabel(ahead: number | null | undefined, behind: number | null | undefined): string {
    const parts: string[] = [];
    if (behind) parts.push(`${behind} ahead of running`);
    if (ahead) parts.push(`${ahead} behind running`);
    return parts.length ? parts.join(' · ') : 'Same commit as running';
  }

  async refreshStatus(): Promise<void> {
    if (this.refreshInFlight()) return;
    this.refreshInFlight.set(true);
    try {
      await Promise.all([this.client.refreshNow(), this.refreshHistory()]);
    } finally {
      this.refreshInFlight.set(false);
    }
  }

  async trigger(): Promise<void> {
    if (!this.canTrigger()) return;
    this.triggerInFlight.set(true);
    try {
      await this.client.trigger('manual via update center', false);
      // The block-modal will own the running UI from here; we keep the
      // drawer open but let the block-modal's z-index sit above it.
      this.refreshHistory();
    } catch (err: unknown) {
      this.errors.show({
        title: 'Update trigger failed',
        message: err instanceof Error ? err.message : 'Could not reach UpdateService at :5039.',
      });
    } finally {
      this.triggerInFlight.set(false);
    }
  }

  private async refreshHistory(): Promise<void> {
    try {
      const list = await this.client.readHistory(15);
      // newest first
      this.history.set([...list].reverse());
    } catch {
      /* leave previous list */
    }
  }
}
