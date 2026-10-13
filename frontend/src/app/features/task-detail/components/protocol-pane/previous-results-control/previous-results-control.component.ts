import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import type { ResultHistoryDocument, ResultHistoryEntry, ResultHistoryLegacyState } from '../../../../../models/task.model';
import { TaskService } from '../../../../../services/task.service';
import { formatDateTimeUtc } from '../../../../../services/format.util';
import { DisclosureMarkerComponent } from '../../../../../components/disclosure-marker/disclosure-marker.component';

@Component({
  selector: 'app-previous-results-control',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DisclosureMarkerComponent],
  templateUrl: './previous-results-control.component.html',
  styleUrl: './previous-results-control.component.scss',
})
export class PreviousResultsControlComponent {
  private readonly tasks = inject(TaskService);

  readonly jobId = input.required<string>();
  readonly watchPath = input<string | null>(null);
  readonly resultSelected = output<ResultHistoryDocument | null>();

  readonly entries = signal<ResultHistoryEntry[]>([]);
  readonly open = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly selected = signal<ResultHistoryEntry | null>(null);
  /** Whether older, pre-snapshot Results from workspace git are known yet. */
  readonly legacy = signal<ResultHistoryLegacyState>('complete');
  private refetchedPending = false;

  private readonly reset = effect(() => {
    this.jobId();
    this.watchPath();
    this.entries.set([]);
    this.legacy.set('complete');
    this.refetchedPending = false;
    this.open.set(false);
    this.error.set(null);
    this.selected.set(null);
    this.resultSelected.emit(null);
    this.load();
  }, { allowSignalWrites: true });

  toggle(): void {
    this.open.update(value => !value);
    // The backfill runs in the background after the first open; look once
    // more when the menu opens so a finished backfill shows up.
    if (this.open() && this.legacy() === 'pending' && !this.refetchedPending) {
      this.refetchedPending = true;
      this.load({ quiet: true });
    }
  }

  showCurrent(): void {
    this.selected.set(null);
    this.open.set(false);
    this.resultSelected.emit(null);
  }

  select(entry: ResultHistoryEntry): void {
    this.loading.set(true);
    this.error.set(null);
    this.tasks.readResultHistory(this.jobId(), entry.id, this.watchPath() ?? undefined).subscribe({
      next: document => {
        this.selected.set(document.entry);
        this.open.set(false);
        this.loading.set(false);
        this.resultSelected.emit(document);
      },
      error: err => {
        this.error.set(err?.error?.error || err?.message || 'Could not open the previous result.');
        this.loading.set(false);
      },
    });
  }

  timestamp(value: string): string {
    return formatDateTimeUtc(value);
  }

  /** `quiet` keeps the toggle usable while an open menu looks again. */
  private load({ quiet = false } = {}): void {
    if (!quiet) this.loading.set(true);
    this.tasks.getResultHistory(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: list => {
        this.entries.set(list.entries);
        this.legacy.set(list.legacy);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.error?.error || err?.message || 'Could not load previous results.');
        this.loading.set(false);
      },
    });
  }
}
