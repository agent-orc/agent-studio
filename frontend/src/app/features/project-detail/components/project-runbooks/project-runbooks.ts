import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';

interface RunbookDefinition { name: string; steps: string[]; environments: string[] }
interface RunbookStep { name: string; state: string; logRef: string }
interface RunbookRun {
  id: string; name: string; state: string; requestedBy: string; approvedBy: string | null;
  steps: RunbookStep[];
}
interface RunbookLog { lines: { step: string; message: string }[] }

@Component({
  selector: 'app-project-runbooks',
  standalone: true,
  imports: [DisclosureMarkerComponent],
  templateUrl: './project-runbooks.html',
  styleUrl: './project-runbooks.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectRunbooksComponent {
  private readonly http = inject(HttpClient);
  readonly projectName = input.required<string>();
  readonly environmentId = input.required<string>();
  readonly environmentKind = input.required<string>();
  readonly definitions = signal<RunbookDefinition[]>([]);
  readonly runs = signal<RunbookRun[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly openLogId = signal<string | null>(null);
  readonly logLines = signal<RunbookLog['lines']>([]);

  constructor() {
    effect(onCleanup => {
      const project = this.projectName();
      const environment = this.environmentId();
      const subscription = forkJoin({
        definitions: this.http.get<RunbookDefinition[]>(this.base(project)),
        runs: this.http.get<RunbookRun[]>(`${this.base(project)}/runs`, { params: { environmentId: environment } }),
      }).subscribe({
        next: result => {
          this.definitions.set(result.definitions.filter(item => item.environments.length === 0 || item.environments.includes(this.environmentKind())));
          this.runs.set(result.runs);
          this.error.set(null);
        },
        error: () => this.error.set('Runbooks are unavailable.'),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  refresh(): void {
    this.http.get<RunbookRun[]>(`${this.base(this.projectName())}/runs`,
      { params: { environmentId: this.environmentId() } }).subscribe({
      next: runs => { this.runs.set(runs); this.error.set(null); },
      error: () => this.error.set('Runbook history could not be loaded.'),
    });
  }

  start(name: string): void {
    this.mutate(`${this.base(this.projectName()).replace('/runbooks', '')}/environments/${encodeURIComponent(this.environmentId())}/runbooks/${encodeURIComponent(name)}/runs`);
  }

  decide(id: string, action: 'approve' | 'reject'): void {
    this.mutate(`${this.base(this.projectName())}/runs/${encodeURIComponent(id)}/${action}`);
  }

  toggleLog(id: string): void {
    if (this.openLogId() === id) { this.openLogId.set(null); return; }
    this.http.get<RunbookLog>(`${this.base(this.projectName())}/runs/${encodeURIComponent(id)}/log`).subscribe({
      next: log => { this.logLines.set(log.lines); this.openLogId.set(id); },
      error: () => this.error.set('Runbook log could not be loaded.'),
    });
  }

  private mutate(url: string): void {
    this.busy.set(true);
    this.http.post(url, {}).subscribe({
      next: () => { this.busy.set(false); this.refresh(); },
      error: () => { this.busy.set(false); this.error.set('The runbook action was refused. Refresh and check approvals.'); },
    });
  }

  private base(project: string): string {
    return `/api/projects/${encodeURIComponent(project)}/deploy/runbooks`;
  }
}
