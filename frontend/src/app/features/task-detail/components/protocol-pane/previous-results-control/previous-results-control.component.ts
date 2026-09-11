import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import type { ResultHistoryDocument, ResultHistoryEntry } from '../../../../../models/task.model';
import { TaskService } from '../../../../../services/task.service';
import { formatDateTimeUtc } from '../../../../../services/format.util';

@Component({
  selector: 'app-previous-results-control',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
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

  private readonly reset = effect(() => {
    this.jobId();
    this.watchPath();
    this.entries.set([]);
    this.open.set(false);
    this.error.set(null);
    this.selected.set(null);
    this.resultSelected.emit(null);
    this.load();
  }, { allowSignalWrites: true });

  toggle(): void {
    this.open.update(value => !value);
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

  private load(): void {
    this.loading.set(true);
    this.tasks.getResultHistory(this.jobId(), this.watchPath() ?? undefined).subscribe({
      next: entries => {
        this.entries.set(entries ?? []);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.error?.error || err?.message || 'Could not load previous results.');
        this.loading.set(false);
      },
    });
  }
}
