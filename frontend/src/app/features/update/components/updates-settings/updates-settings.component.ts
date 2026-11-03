import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { UpdateClientService } from '../../../../services/update.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Update state on the shared indicator (docs/quality/design/status-indicator.md). */
const updateTones: Readonly<Record<'running' | 'behind' | 'failed' | 'ok' | 'unknown', StatusTone>> = {
  ok: 'success',
  behind: 'warning',
  running: 'running',
  failed: 'danger',
  unknown: 'neutral',
};

/**
 * AGT-2035 — compact Updates section of the consolidated Workspace-settings
 * view.
 *
 * The old Updates block (in the sidebar Settings panel) was a four-row text
 * wall: Current / Phase / Behind / message. Per the operator direction it is
 * redesigned to **one status line + one action**: a single readable summary of
 * where stable is, the one primary button that is meaningful right now (update
 * / check), and a subtle link into the full update history for the detail.
 */
@Component({
  selector: 'app-updates-settings',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './updates-settings.component.html',
  styleUrl: './updates-settings.component.scss',
})
export class UpdatesSettingsComponent {
  readonly updateClient = inject(UpdateClientService);

  /** One-line human status: the single sentence the row shows. */
  readonly statusLine = computed<string>(() => {
    const st = this.updateClient.status();
    if (!st) return 'Update service status not yet polled.';
    if (st.isRunning) return `Updating… ${st.phase}`;
    const behind = this.updateClient.behindBy();
    const head = st.headLocal || '—';
    // The tone shows a failed last run, so the text has to say it too.
    const failed = st.phase === 'failed' ? 'Last update failed · ' : '';
    if (behind > 0) {
      return `${failed}${behind} ${behind === 1 ? 'commit' : 'commits'} behind · ${head}`;
    }
    return `${failed}Up to date · ${head}`;
  });

  /** Semantic state behind the status indicator tone. */
  readonly state = computed<'running' | 'behind' | 'failed' | 'ok' | 'unknown'>(() => {
    const st = this.updateClient.status();
    if (!st) return 'unknown';
    if (st.isRunning) return 'running';
    if (st.phase === 'failed') return 'failed';
    if (this.updateClient.behindBy() > 0) return 'behind';
    return 'ok';
  });

  readonly tone = computed<StatusTone>(() => updateTones[this.state()]);

  /** Primary-button label flips with the current state. */
  readonly actionLabel = computed<string>(() => {
    if (this.updateClient.isRunning()) return 'Update running…';
    return this.updateClient.behindBy() > 0 ? 'Update stable now' : 'Check for updates';
  });

  /**
   * Same guard as the old panel: only run the destructive stable update when
   * actually behind (or forced); otherwise a "Check for updates" click just
   * polls origin/main.
   */
  triggerUpdate(force = false): void {
    this.updateClient.openCenter();
    if (force || this.updateClient.behindBy() > 0) {
      void this.updateClient.trigger(null, force);
    } else {
      void this.updateClient.refreshNow();
    }
  }

  openUpdateCenter(): void {
    this.updateClient.openCenter();
    void this.updateClient.refreshNow();
  }
}
