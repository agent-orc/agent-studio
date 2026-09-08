import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { formatRetentionBytes, type RetentionRunDetail, type RetentionRunSummary, type RetentionSchedule } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';
import { RetentionReportTableComponent } from '../retention-report-table/retention-report-table.component';

@Component({
  selector: 'app-retention-run-history',
  standalone: true,
  imports: [RetentionReportTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './retention-run-history.component.html',
  styleUrl: './retention-run-history.component.scss',
})
export class RetentionRunHistoryComponent implements OnInit {
  private readonly retention = inject(RetentionService);
  readonly runs = signal<RetentionRunSummary[]>([]);
  readonly schedule = signal<RetentionSchedule | null>(null);
  readonly details = signal<Record<string, RetentionRunDetail>>({});
  readonly expandedRunId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void { void this.reload(); }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const [runs, schedule] = await Promise.all([
        firstValueFrom(this.retention.listRuns()),
        firstValueFrom(this.retention.getSchedule()).catch(() => null),
      ]);
      this.runs.set(runs);
      this.schedule.set(schedule);
    } catch {
      this.error.set('Run history could not be loaded.');
    } finally {
      this.loading.set(false);
    }
  }

  async toggle(run: RetentionRunSummary): Promise<void> {
    if (this.expandedRunId() === run.id) {
      this.expandedRunId.set(null);
      return;
    }
    this.expandedRunId.set(run.id);
    if (this.details()[run.id]) return;
    try {
      const detail = await firstValueFrom(this.retention.getRun(run.id));
      this.details.update(current => ({ ...current, [run.id]: detail }));
    } catch {
      this.error.set('The full run report could not be loaded.');
    }
  }

  date(value: string | null): string {
    if (!value) return '–';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '–' : date.toLocaleString();
  }

  duration(run: RetentionRunSummary): string {
    if (!run.finishedAt) return '–';
    const duration = new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime();
    if (!Number.isFinite(duration) || duration < 0) return '–';
    return duration < 1000 ? `${duration} ms` : `${(duration / 1000).toFixed(1)} s`;
  }

  bytes(value: number): string { return formatRetentionBytes(value); }

  scheduleText(): string {
    const schedule = this.schedule();
    if (!schedule) return 'Schedule unavailable';
    if (!schedule.enabled) return 'Scheduled retention is off';
    const hour = String(schedule.serverLocalHour).padStart(2, '0');
    return `Daily at ${hour}:00 server time · Next run ${this.date(schedule.nextRunAt)}`;
  }

  result(run: RetentionRunSummary): string {
    if (!run.finishedAt) return 'Running';
    if ((run.errorCount ?? this.details()[run.id]?.errors.length ?? 0) > 0) return 'Completed with errors';
    if ((run.warningCount ?? this.details()[run.id]?.warnings.length ?? 0) > 0) return 'Completed with warnings';
    return 'Completed';
  }
}
