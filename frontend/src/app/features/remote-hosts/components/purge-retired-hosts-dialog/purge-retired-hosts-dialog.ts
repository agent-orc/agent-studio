import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { purgeRefusalLabel, type PurgeRetiredClientCandidate } from '../../models/remote-host.model';

type Phase = 'form' | 'previewing' | 'preview' | 'purging' | 'report' | 'error';

/**
 * Toolbar "Delete retired..." dialog (AGT-2748): dry-run preview of every
 * retired identity matching a name prefix (defaults to "e2e-", the
 * convention leftover e2e runs already use), then an explicit apply step.
 * Each candidate carries the same guard the single-row Delete action
 * enforces, so a still-leased or still-online straggler is reported as
 * blocked rather than silently skipped.
 */
@Component({
  selector: 'app-purge-retired-hosts-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent],
  templateUrl: './purge-retired-hosts-dialog.html',
  styleUrl: './purge-retired-hosts-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PurgeRetiredHostsDialogComponent {
  private readonly service = inject(RemoteHostsService);

  readonly closed = output<void>();

  readonly prefix = signal('e2e-');
  readonly phase = signal<Phase>('form');
  readonly candidates = signal<readonly PurgeRetiredClientCandidate[]>([]);
  readonly deletedCount = signal(0);
  readonly errorText = signal('');

  readonly eligibleCount = computed(() => this.candidates().filter(candidate => candidate.eligible).length);
  readonly blockedCandidates = computed(() => this.candidates().filter(candidate => !candidate.deleted));

  readonly refusalLabel = purgeRefusalLabel;

  setPrefix(value: string): void {
    this.prefix.set(value);
  }

  preview(): void {
    this.phase.set('previewing');
    this.service.purgeRetired(this.prefix().trim(), true).subscribe({
      next: result => {
        this.candidates.set(result.candidates);
        this.phase.set('preview');
      },
      error: err => {
        this.errorText.set(err?.error?.error || err?.message || 'Failed to preview retired clients.');
        this.phase.set('error');
      },
    });
  }

  confirmPurge(): void {
    this.phase.set('purging');
    this.service.purgeRetired(this.prefix().trim(), false).subscribe({
      next: result => {
        this.candidates.set(result.candidates);
        this.deletedCount.set(result.deletedCount);
        this.phase.set('report');
      },
      error: err => {
        this.errorText.set(err?.error?.error || err?.message || 'The purge failed. Nothing was deleted.');
        this.phase.set('error');
      },
    });
  }

  backToForm(): void {
    this.phase.set('form');
  }
}
