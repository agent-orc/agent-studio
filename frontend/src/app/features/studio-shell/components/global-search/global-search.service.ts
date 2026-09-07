import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { CLIENT_ID } from '../../../../services/client-id.interceptor';

export type SearchDomain = 'tasks' | 'commits' | 'files';

export const SEARCH_DOMAINS: readonly SearchDomain[] = ['tasks', 'commits', 'files'];

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

/**
 * One delivered slice of a streamed search. `repository` is null for the task
 * domain, which arrives in one piece from the warm index; the git domains send
 * one chunk per checkout with `completed` of `total`, so the palette can say how
 * far the sweep has got instead of showing one opaque spinner.
 */
export interface GlobalSearchChunk {
  domain: SearchDomain;
  items: GlobalSearchItem[];
  repository: string | null;
  completed: number;
  total: number;
  error: string | null;
}

@Injectable({ providedIn: 'root' })
export class GlobalSearchService {
  private readonly http = inject(HttpClient);

  /** Single-response search. Kept for callers that cannot consume a stream. */
  search(query: string) {
    const params = new HttpParams()
      .set('q', query)
      .set('domains', SEARCH_DOMAINS.join(','))
      .set('limit', 20);
    return this.http.get<GlobalSearchResponse>('/api/search', { params });
  }

  /**
   * Reads the server-sent-events search and hands each chunk to `onChunk` as it
   * arrives. Aborting `signal` closes the connection, which is what cancels the
   * sweep on the server.
   *
   * This uses `fetch` rather than `HttpClient` because Angular's client buffers
   * the whole body, which would defeat the point of streaming. The two things
   * the API cares about on a GET are reproduced here: the attribution client id,
   * and cookies, which `fetch` sends for same-origin requests by default.
   */
  async searchStream(
    query: string,
    limit: number,
    signal: AbortSignal,
    onChunk: (chunk: GlobalSearchChunk) => void,
  ): Promise<void> {
    const params = new URLSearchParams({ q: query, domains: SEARCH_DOMAINS.join(','), limit: String(limit) });
    const response = await fetch(`/api/search/stream?${params.toString()}`, {
      signal,
      headers: { Accept: 'text/event-stream', 'X-Client-Id': CLIENT_ID },
    });
    if (!response.ok || !response.body) throw new Error(`Search stream failed with status ${response.status}.`);

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      // SSE frames are separated by a blank line; the server writes exactly one
      // single-line JSON payload per frame.
      let boundary = buffer.indexOf('\n\n');
      while (boundary >= 0) {
        const chunk = parseFrame(buffer.slice(0, boundary));
        buffer = buffer.slice(boundary + 2);
        if (chunk) onChunk(chunk);
        boundary = buffer.indexOf('\n\n');
      }
    }
  }
}

/** Returns the chunk carried by one SSE frame, or null for the terminating `done` frame. */
function parseFrame(frame: string): GlobalSearchChunk | null {
  let event = '';
  let data = '';
  for (const line of frame.split('\n')) {
    if (line.startsWith('event:')) event = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trim();
  }
  if (!data || !SEARCH_DOMAINS.includes(event as SearchDomain)) return null;
  return JSON.parse(data) as GlobalSearchChunk;
}
