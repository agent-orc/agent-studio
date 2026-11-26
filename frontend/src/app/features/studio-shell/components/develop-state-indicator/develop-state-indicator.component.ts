import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { TestQuarantine } from '../../../../models/test-quarantine.model';

interface DevelopStateRow {
  project: string;
  developState: 'green' | 'running' | 'red' | null;
  lastGreenSha: string | null;
  quarantinedCount?: number;
  lineStop?: string | null;
  quarantines?: TestQuarantine[];
}

@Component({
  selector: 'app-develop-state-indicator',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './develop-state-indicator.component.html',
  styleUrl: './develop-state-indicator.component.scss',
})
export class DevelopStateIndicatorComponent implements OnDestroy {
  readonly project = input<string | null>(null);
  readonly row = signal<DevelopStateRow | null>(null);
  readonly label = computed(() => {
    const row = this.row();
    if (row?.lineStop) return 'Develop stopped · ' + row.lineStop;
    if (row?.developState === 'running') return 'Develop running';
    const count = row?.quarantinedCount ?? row?.quarantines?.length ?? 0;
    return 'Develop green' + (count ? ` · ${count} quarantined` : '');
  });
  readonly tone = computed<StatusTone>(() => this.row()?.lineStop ? 'danger'
    : this.row()?.developState === 'running' ? 'running'
    : (this.row()?.quarantinedCount ?? this.row()?.quarantines?.length ?? 0) > 0 ? 'warning' : 'success');
  readonly detail = computed(() => {
    const row = this.row();
    return `${row?.project} last green develop SHA: ${row?.lastGreenSha || 'not recorded'}`
      + (row?.quarantines ?? []).map(entry => `\n${entry.test} · ${entry.kind} · ends ${entry.expiresAtUtc}`).join('');
  });
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
          : rows.find(item => item.lineStop)
            || rows.find(item => item.developState === 'running')
            || rows.find(item => item.developState === 'green') || rows[0];
        this.row.set(row ?? null);
      },
      error: () => { if (version === this.version) this.row.set(null); },
    });
  }
}
