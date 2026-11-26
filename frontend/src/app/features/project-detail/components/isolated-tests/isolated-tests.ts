import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import type { TestQuarantine } from '../../../../models/test-quarantine.model';

@Component({
  selector: 'app-isolated-tests', standalone: true,
  imports: [DatePipe, LoadingSurfaceComponent, StatusIndicatorComponent],
  templateUrl: './isolated-tests.html', styleUrl: './isolated-tests.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IsolatedTestsComponent {
  readonly projectName = input.required<string>();
  readonly openCard = output<string>();
  readonly entries = signal<TestQuarantine[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);
  private readonly http = inject(HttpClient);
  private readonly now = signal(Date.now());
  private generation = 0;
  readonly active = computed(() => this.entries().filter(entry => !entry.liftedAtUtc && !entry.resolvedOn
    && Date.parse(entry.expiresAtUtc) > this.now()));

  constructor() {
    effect(() => this.load(this.projectName()));
    const timer = setInterval(() => { this.now.set(Date.now()); this.load(this.projectName(), false); }, 30_000);
    inject(DestroyRef).onDestroy(() => { clearInterval(timer); ++this.generation; });
  }

  load(project: string, reset = true): void {
    const generation = ++this.generation;
    if (reset) { this.entries.set([]); this.loading.set(true); }
    this.error.set(false);
    this.http.get<TestQuarantine[]>(`/api/projects/${encodeURIComponent(project)}/test-quarantines`).subscribe({
      next: entries => {
        if (generation !== this.generation) return;
        this.entries.set(entries); this.now.set(Date.now()); this.loading.set(false);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.error.set(true); this.loading.set(false);
      },
    });
  }

  endsIn(entry: TestQuarantine): string {
    const minutes = Math.max(1, Math.ceil((Date.parse(entry.expiresAtUtc) - this.now()) / 60_000));
    return minutes < 60 ? `${minutes} min` : minutes < 1440 ? `${Math.ceil(minutes / 60)} h` : `${Math.ceil(minutes / 1440)} d`;
  }
}
