import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export type SearchDomain = 'tasks' | 'commits' | 'files';

export interface GlobalSearchItem {
  domain: SearchDomain;
  projectName: string;
  projectColor: string;
  title: string;
  subtitle: string;
  taskKey?: string;
  lane?: string;
  sha?: string;
  path?: string;
  isWiki?: boolean;
}

export interface GlobalSearchResponse {
  query: string;
  tasks: GlobalSearchItem[];
  commits: GlobalSearchItem[];
  files: GlobalSearchItem[];
  errors: Record<string, string>;
  durationMs: number;
}

/** Opens the stream and states how many repositories the git domains will visit. */
export interface SearchMetaFrame {
  query: string;
  repositories: number;
}

/** Task matches from the warm backend index. Always arrives before any repository frame. */
export interface SearchTasksFrame {
  items: GlobalSearchItem[];
  durationMs: number;
  error: string | null;
}

/** One repository's git matches. `index` counts completions, so it is the progress counter. */
export interface SearchRepositoryFrame {
  name: string;
  index: number;
  total: number;
  commits: GlobalSearchItem[];
  files: GlobalSearchItem[];
  durationMs: number;
  commitsCache: string;
  filesCache: string;
  commitsError: string | null;
  filesError: string | null;
}

export interface SearchDoneFrame {
  durationMs: number;
  errors: Record<string, string>;
}

export type GlobalSearchStreamEvent =
  | { kind: 'meta'; payload: SearchMetaFrame }
  | { kind: 'tasks'; payload: SearchTasksFrame }
  | { kind: 'repository'; payload: SearchRepositoryFrame }
  | { kind: 'done'; payload: SearchDoneFrame }
  | { kind: 'failed' };

@Injectable({ providedIn: 'root' })
export class GlobalSearchService {
  private readonly http = inject(HttpClient);

  /** Single-response search. Kept for callers that want one payload. */
  search(query: string) {
    const params = new HttpParams()
      .set('q', query)
      .set('domains', 'tasks,commits,files')
      .set('limit', 20);
    return this.http.get<GlobalSearchResponse>('/api/search', { params });
  }

  /**
   * Streamed search. Tasks answer from the warm index within a frame and every
   * repository arrives as it finishes, so the palette can show progress instead
   * of one spinner for the whole search.
   *
   * Unsubscribing closes the EventSource, which aborts the HTTP request; the
   * backend cancels the search and kills the git children still walking. That
   * is how a keystroke cancels the previous query.
   */
  searchStream(query: string): Observable<GlobalSearchStreamEvent> {
    return new Observable<GlobalSearchStreamEvent>(subscriber => {
      const params = new URLSearchParams({ q: query, domains: 'tasks,commits,files', limit: '20' });
      const source = new EventSource(`/api/search/stream?${params.toString()}`);
      let settled = false;
      const finish = () => {
        if (settled) return;
        settled = true;
        source.close();
        subscriber.complete();
      };

      const relay = (name: 'meta' | 'tasks' | 'repository' | 'done') =>
        source.addEventListener(name, (event: MessageEvent<string>) => {
          try {
            subscriber.next({ kind: name, payload: JSON.parse(event.data) } as GlobalSearchStreamEvent);
          } catch {
            subscriber.next({ kind: 'failed' });
            finish();
            return;
          }
          if (name === 'done') finish();
        });

      relay('meta');
      relay('tasks');
      relay('repository');
      relay('done');

      // A protocol-level failure. Without closing here EventSource would keep
      // reconnecting and re-run the whole search behind the operator's back.
      source.onerror = () => {
        if (settled) return;
        subscriber.next({ kind: 'failed' });
        finish();
      };

      return () => {
        settled = true;
        source.close();
      };
    });
  }
}
