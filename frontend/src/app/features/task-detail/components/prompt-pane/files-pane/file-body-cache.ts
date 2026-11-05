import { signal } from '@angular/core';
import type { Subscription } from 'rxjs';
import type { TaskService } from '../../../../../services/task.service';

/** File bodies live for one Docs component instance and one selected task. */
export class FileBodyCache {
  readonly content = signal<Map<string, string | null>>(new Map());
  readonly loading = signal<Set<string>>(new Set());
  private readonly fetched = new Set<string>();
  private readonly requests = new Map<string, Subscription>();

  constructor(private readonly jobs: TaskService) {}

  reset(): void {
    for (const request of this.requests.values()) request.unsubscribe();
    this.requests.clear();
    this.fetched.clear();
    this.content.set(new Map());
    this.loading.set(new Set());
  }

  read(jobId: string, name: string, watchPath?: string, retry = false): void {
    if (this.requests.get(name) && !this.requests.get(name)?.closed) return;
    if (this.fetched.has(name) && !retry) return;
    this.fetched.add(name);
    this.setLoading(name, true);
    const request = this.jobs.readJobFile(jobId, name, watchPath).subscribe({
      next: text => {
        this.setContent(name, typeof text === 'string' ? text : '');
        this.setLoading(name, false);
      },
      error: () => {
        this.setContent(name, null);
        this.setLoading(name, false);
      },
    });
    if (!request.closed) this.requests.set(name, request);
  }

  private setContent(name: string, value: string | null): void {
    this.content.update(content => new Map(content).set(name, value));
  }

  private setLoading(name: string, active: boolean): void {
    const next = new Set(this.loading());
    if (active) next.add(name);
    else next.delete(name);
    this.loading.set(next);
  }
}
