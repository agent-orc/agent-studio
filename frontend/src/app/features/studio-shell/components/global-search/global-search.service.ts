import { Injectable } from '@angular/core';
import { sessionFetch } from '../../../../services/session-fetch';

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

/** Task matches. Served from the in-memory index, so this frame lands first. */
export interface SearchTasksFrame {
  items: GlobalSearchItem[];
  durationMs: number;
  error: string | null;
}

/** How many repositories the git domains will visit. */
export interface SearchProgressFrame {
  completed: number;
  total: number;
}

/** One repository's matches, delivered the moment that repository finishes. */
export interface SearchRepositoryFrame {
  projectName: string;
  completed: number;
  total: number;
  commits: GlobalSearchItem[];
  files: GlobalSearchItem[];
  durationMs: number;
  fromCache: boolean;
  failedDomains: SearchDomain[];
}

export interface SearchDoneFrame {
  durationMs: number;
  tasksMs: number;
  repositoriesMs: number;
  repositories: number;
}

export type GlobalSearchFrame =
  | { event: 'tasks'; data: SearchTasksFrame }
  | { event: 'progress'; data: SearchProgressFrame }
  | { event: 'repository'; data: SearchRepositoryFrame }
  | { event: 'done'; data: SearchDoneFrame };

@Injectable({ providedIn: 'root' })
export class GlobalSearchService {
  /**
   * Streams `GET /api/search/stream` as server-sent events.
   *
   * Fetch rather than `EventSource` because the palette must be able to abort a
   * search the moment the operator types again, and because the session and
   * attribution headers ride along through {@link sessionFetch}. Aborting the
   * signal closes the response body, which cancels the fan-out server-side.
   */
  async *stream(query: string, signal: AbortSignal): AsyncGenerator<GlobalSearchFrame> {
    const params = new URLSearchParams({ q: query, domains: 'tasks,commits,files', limit: '20' });
    const response = await sessionFetch(`/api/search/stream?${params.toString()}`, { signal });
    if (!response.ok || !response.body) throw new Error(`Search stream failed with status ${response.status}.`);

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    let buffer = '';
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += value;
        // An SSE frame ends at a blank line; anything after it is the start of
        // the next frame and stays in the buffer until its own terminator.
        let boundary = buffer.indexOf('\n\n');
        while (boundary >= 0) {
          const frame = parseFrame(buffer.slice(0, boundary));
          buffer = buffer.slice(boundary + 2);
          if (frame) yield frame;
          boundary = buffer.indexOf('\n\n');
        }
      }
    } finally {
      reader.cancel().catch(() => undefined);
    }
  }
}

function parseFrame(block: string): GlobalSearchFrame | null {
  let event = '';
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) event = line.slice('event:'.length).trim();
    else if (line.startsWith('data:')) data.push(line.slice('data:'.length).trim());
  }
  if (!event || !data.length) return null;
  try {
    return { event, data: JSON.parse(data.join('\n')) } as GlobalSearchFrame;
  } catch {
    // A truncated frame is not worth failing the whole search over; the next
    // one still arrives.
    return null;
  }
}
