import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

interface DevelopStateRow {
  project: string;
  developState: 'green' | 'running' | 'red' | null;
  lastGreenSha: string | null;
}

@Component({
  selector: 'app-develop-state-indicator',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './develop-state-indicator.component.html',
  styleUrl: './develop-state-indicator.component.scss',
})
export class DevelopStateIndicatorComponent implements OnDestroy {
  readonly project = input<string | null>(null);
  readonly row = signal<DevelopStateRow | null>(null);
  private readonly http = inject(HttpClient);
  private version = 0;
  private readonly poll = setInterval(() => this.refresh(), 30_000);

  constructor() { effect(() => { this.project(); this.refresh(); }); }
  ngOnDestroy(): void { clearInterval(this.poll); this.version++; }

  private refresh(): void {
    const version = ++this.version;
    const project = this.project();
    this.http.get<DevelopStateRow[]>('/api/develop-states').subscribe({
      next: response => {
        if (version !== this.version) return;
        const rows = Array.isArray(response) ? response : [];
        const row = project ? rows.find(item => item.project === project)
          : rows.find(item => item.developState === 'red')
            || rows.find(item => item.developState === 'running')
            || rows.find(item => item.developState === 'green') || rows[0];
        this.row.set(row ?? null);
      },
      error: () => { if (version === this.version) this.row.set(null); },
    });
  }
}
