import { Injectable } from '@angular/core';
import { sessionFetch } from '../../../../services/session-fetch';

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

/** Repository count for the "i of n repositories" progress line. */
export interface GlobalSearchStart {
  query: string;
  repositories: number;
  domains: SearchDomain[];
}

/** One domain's results from one repository, or the whole task domain. */
export interface GlobalSearchChunk {
  domain: SearchDomain;
  projectName: string;
  items: GlobalSearchItem[];
  durationMs: number;
  cacheHit: boolean;
}

export interface GlobalSearchProgress {
  domain: SearchDomain;
  completed: number;
  total: number;
}

export interface GlobalSearchFailure {
  domain: SearchDomain;
  projectName: string | null;
  message: string;
}

export interface GlobalSearchSummary {
  durationMs: number;
  domainDurationMs: Record<string, number>;
  repositories: number;
}

export interface GlobalSearchStreamHandlers {
  start?(event: GlobalSearchStart): void;
  chunk?(event: GlobalSearchChunk): void;
  progress?(event: GlobalSearchProgress): void;
  failure?(event: GlobalSearchFailure): void;
  done?(event: GlobalSearchSummary): void;
}

@Injectable({ providedIn: 'root' })
export class GlobalSearchService {
  /**
   * Reads `/api/search/stream` as Server-Sent Events and dispatches each frame
   * as it arrives. Deliberately Fetch rather than `EventSource`: the palette
   * must abort the in-flight search on every keystroke and on Escape, and
   * `EventSource` neither takes an `AbortSignal` nor carries the session
   * headers `sessionFetch` attaches.
   *
   * Resolves when the server closes the stream. An abort resolves quietly -
   * a cancelled search is not an error - while a transport failure rejects.
   */
  async stream(
    query: string,
    handlers: GlobalSearchStreamHandlers,
    signal: AbortSignal,
    limit = 20,
  ): Promise<void> {
    const params = new URLSearchParams({
      q: query,
      domains: SEARCH_DOMAINS.join(','),
      limit: String(limit),
    });

    let response: Response;
    try {
      response = await sessionFetch(`/api/search/stream?${params}`, { signal });
    } catch (error) {
      if (signal.aborted) return;
      throw error;
    }
    if (!response.ok || !response.body) {
      throw new Error(`Search stream failed with status ${response.status}`);
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        // Frames are separated by a blank line; the tail is a partial frame.
        const frames = buffer.split('\n\n');
        buffer = frames.pop() ?? '';
        for (const frame of frames) dispatch(frame, handlers);
      }
    } catch (error) {
      if (signal.aborted) return;
      throw error;
    } finally {
      // Releasing the lock lets the aborted body be collected instead of
      // holding the connection open until GC.
      reader.releaseLock();
    }
  }
}

function dispatch(frame: string, handlers: GlobalSearchStreamHandlers): void {
  let name = '';
  let data = '';
  for (const line of frame.split('\n')) {
    if (line.startsWith('event: ')) name = line.slice(7).trim();
    else if (line.startsWith('data: ')) data = line.slice(6);
  }
  if (!name || !data) return;

  let payload: unknown;
  try {
    payload = JSON.parse(data);
  } catch {
    // A truncated frame is not worth tearing the whole search down for.
    return;
  }

  switch (name) {
    case 'start': handlers.start?.(payload as GlobalSearchStart); break;
    case 'chunk': handlers.chunk?.(payload as GlobalSearchChunk); break;
    case 'progress': handlers.progress?.(payload as GlobalSearchProgress); break;
    case 'error': handlers.failure?.(payload as GlobalSearchFailure); break;
    case 'done': handlers.done?.(payload as GlobalSearchSummary); break;
  }
}
