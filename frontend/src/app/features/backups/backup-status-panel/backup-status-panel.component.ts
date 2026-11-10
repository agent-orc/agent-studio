import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { take } from 'rxjs';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import {
  formatAge, formatBytes, isBackupOverview, itemOffHostLabel, offHostLabel, offHostListingSummary, offHostTone, overviewLabel, overviewTone,
  verificationLabel, verificationTone, type BackupOverview,
} from '../backup-status.model';

/**
 * Installation backups on the Operations page (AGT-3574): the last good
 * backup with its age and size, the last restore verification, the
 * off-host copy, and the warnings of `GET /api/v1/management/backups`.
 * AGT-3734 adds the restore receipt of an offline restore, the archives at
 * the off-host target that the local catalog does not know, and what the
 * last verification proved and did not prove.
 * Accounts without the management role get 401/403 and see nothing.
 */
@Component({
  selector: 'app-backup-status-panel',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './backup-status-panel.component.html',
  styleUrl: './backup-status-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BackupStatusPanelComponent {
  private readonly http = inject(HttpClient);
  private generation = 0;
  readonly overview = signal<BackupOverview | null>(null);
  readonly error = signal<string | null>(null);
  readonly hidden = signal(false);
  readonly overviewTone = overviewTone;
  readonly overviewLabel = overviewLabel;
  readonly offHostLabel = offHostLabel;
  readonly offHostTone = offHostTone;
  readonly offHostListingSummary = offHostListingSummary;
  readonly verificationTone = verificationTone;
  readonly verificationLabel = verificationLabel;
  readonly itemOffHostLabel = itemOffHostLabel;
  readonly formatAge = formatAge;
  readonly formatBytes = formatBytes;

  constructor() { this.load(); }

  load(): void {
    const generation = ++this.generation;
    this.error.set(null);
    this.http.get<BackupOverview>('/api/v1/management/backups').pipe(take(1)).subscribe({
      next: value => {
        if (generation !== this.generation) return;
        // An installation without the backup API answers something else; show nothing then.
        if (!isBackupOverview(value)) {
          this.overview.set(null);
          this.hidden.set(true);
          return;
        }
        this.overview.set(value);
        this.hidden.set(false);
      },
      error: (failure: unknown) => {
        if (generation !== this.generation) return;
        const status = failure instanceof HttpErrorResponse ? failure.status : 0;
        if (status === 401 || status === 403 || status === 404) {
          this.hidden.set(true);
          return;
        }
        this.error.set('Backup state could not be loaded.');
      },
    });
  }
}
