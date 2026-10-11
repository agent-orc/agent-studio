import { Injectable, inject, signal, type Signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom, type Observable } from 'rxjs';
import type { BatchGateConfiguration, BatchGateSettings, BatchGateState } from '../models/batch-gate.model';

interface ProjectFeed {
  state: ReturnType<typeof signal<BatchGateState | null>>;
  error: ReturnType<typeof signal<string | null>>;
  consumers: number;
  timer?: ReturnType<typeof setInterval>;
  inFlight: boolean;
}

@Injectable({ providedIn: 'root' })
export class BatchGateService {
  private readonly http = inject(HttpClient);
  private readonly feeds = new Map<string, ProjectFeed>();
  private readonly visibilityChanged = () => {
    for (const [project, feed] of this.feeds) this.schedule(project, feed);
  };

  constructor() {
    document.addEventListener('visibilitychange', this.visibilityChanged);
  }

  private url(project: string): string {
    return `/api/projects/${encodeURIComponent(project)}/batch-gate`;
  }

  private feed(project: string): ProjectFeed {
    let feed = this.feeds.get(project);
    if (!feed) {
      feed = { state: signal<BatchGateState | null>(null), error: signal<string | null>(null), consumers: 0, inFlight: false };
      this.feeds.set(project, feed);
    }
    return feed;
  }

  state(project: string): Signal<BatchGateState | null> { return this.feed(project).state.asReadonly(); }
  error(project: string): Signal<string | null> { return this.feed(project).error.asReadonly(); }

  /** One request and timer per visible project, regardless of consumer count. */
  watch(project: string): () => void {
    const feed = this.feed(project);
    feed.consumers++;
    this.schedule(project, feed);
    return () => {
      feed.consumers = Math.max(0, feed.consumers - 1);
      this.schedule(project, feed);
    };
  }

  private schedule(project: string, feed: ProjectFeed): void {
    if (feed.consumers === 0 || document.hidden) {
      if (feed.timer) clearInterval(feed.timer);
      feed.timer = undefined;
      return;
    }
    if (feed.timer) return;
    void this.refresh(project);
    feed.timer = setInterval(() => void this.refresh(project), 15_000);
  }

  async refresh(project: string): Promise<void> {
    const feed = this.feed(project);
    if (feed.inFlight) return;
    feed.inFlight = true;
    try {
      feed.state.set(await firstValueFrom(this.http.get<BatchGateState>(`${this.url(project)}/state?history=10`)));
      feed.error.set(null);
    } catch {
      feed.error.set('Batch verification is temporarily unavailable.');
    } finally {
      feed.inFlight = false;
    }
  }

  getSettings(project: string): Observable<BatchGateConfiguration> {
    return this.http.get<BatchGateConfiguration>(this.url(project));
  }
  putSettings(project: string, settings: BatchGateSettings): Observable<BatchGateConfiguration> {
    return this.http.put<BatchGateConfiguration>(this.url(project), settings);
  }
  closeNow(project: string): Observable<unknown> {
    return this.http.post(this.url(project) + '/close-now', {});
  }
  eject(project: string, taskKey: string): Observable<unknown> {
    return this.http.post(`${this.url(project)}/members/${encodeURIComponent(taskKey)}/eject`, {});
  }
}
