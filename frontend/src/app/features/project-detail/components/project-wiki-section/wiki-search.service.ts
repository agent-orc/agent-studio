import { Injectable, computed, inject, signal } from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WikiSearchResponse } from '../../../../models/project-docs.model';

/** Debounce before a typed query reaches the backend. */
const WIKI_SEARCH_DEBOUNCE_MS = 300;

/** Shortest query worth searching for. */
const WIKI_SEARCH_MIN_LENGTH = 2;

/**
 * Knowledge search state: a debounced lexical query with an on-demand semantic
 * expansion. Split out of `project-wiki-section.ts` in AGT-2819.
 *
 * Every response carries a sequence number, so a slow answer to an abandoned
 * query can never overwrite a newer one. Provided per Knowledge section (not
 * app-wide) so two open sections do not share a query.
 */
@Injectable()
export class WikiSearchService {
  private readonly docs = inject(ProjectDocsService);

  readonly query = signal('');
  readonly response = signal<WikiSearchResponse | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly semanticLoading = signal(false);
  readonly semanticRequested = signal(false);

  /** True once the query is long enough that the results pane takes over. */
  readonly active = computed(() => this.query().trim().length >= WIKI_SEARCH_MIN_LENGTH);

  readonly minimumLength = WIKI_SEARCH_MIN_LENGTH;

  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private seq = 0;

  onQueryChange(project: string, value: string): void {
    this.query.set(value);
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    this.debounceTimer = null;
    this.seq++; // invalidate any in-flight response for the old query
    this.semanticRequested.set(false);
    this.error.set(null);
    const query = value.trim();
    if (query.length < WIKI_SEARCH_MIN_LENGTH) {
      this.response.set(null);
      this.loading.set(false);
      this.semanticLoading.set(false);
      return;
    }
    this.loading.set(true);
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = null;
      this.run(project, query, false);
    }, WIKI_SEARCH_DEBOUNCE_MS);
  }

  /** "Expand semantically": re-run the current query with semantic=true. */
  expandSemantically(project: string): void {
    const query = this.query().trim();
    if (query.length < WIKI_SEARCH_MIN_LENGTH || this.semanticLoading()) return;
    this.semanticRequested.set(true);
    this.run(project, query, true);
  }

  /** The top hit, or null when there is nothing to open. */
  topResult(): WikiSearchResponse['results'][number] | null {
    const top = this.response()?.results?.[0];
    return top && this.active() ? top : null;
  }

  /**
   * Re-runs the current query immediately, skipping the debounce. Used after a
   * mutation (a delete) so a gone page cannot linger as a dead, clickable hit.
   */
  rerun(project: string): void {
    const query = this.query().trim();
    if (query.length < WIKI_SEARCH_MIN_LENGTH) return;
    this.run(project, query, this.semanticRequested());
  }

  /** Esc / clearing the box: drop the search so the previous view reappears. */
  reset(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    this.debounceTimer = null;
    this.seq++;
    this.query.set('');
    this.response.set(null);
    this.loading.set(false);
    this.error.set(null);
    this.semanticLoading.set(false);
    this.semanticRequested.set(false);
  }

  private run(project: string, query: string, semantic: boolean): void {
    const seq = ++this.seq;
    if (semantic) this.semanticLoading.set(true);
    else this.loading.set(true);
    this.error.set(null);
    this.docs.searchWiki(project, query, { semantic }).subscribe({
      next: response => {
        if (seq !== this.seq) return;
        this.response.set(response);
        this.loading.set(false);
        this.semanticLoading.set(false);
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        this.semanticLoading.set(false);
        this.error.set('Search failed.');
      },
    });
  }
}
