import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, of } from 'rxjs';
import { catchError, shareReplay, tap } from 'rxjs/operators';
import {
  QuotaApiService,
  type ModelMigrationCatalogView,
  type ModelMigrationProposal,
} from '../../quota';

/**
 * Process-wide cache of the model-migration proposals
 * (`GET /api/cli/model-migrations`).
 *
 * Three surfaces ask the same question — "is this pinned model superseded?" —
 * about the card badge, the project pipeline rows, and the CLI Management
 * routes. They share one cached lookup so the answer cannot differ between
 * them, and so opening a board does not fan out one request per visible card.
 *
 * The proposals are pure data: migration policy stays on the backend, this
 * store only indexes it by model id.
 */
@Injectable({ providedIn: 'root' })
export class ModelMigrationStore {
  private static readonly TTL_MS = 15 * 60 * 1000; // matches the backend catalog cache

  private readonly api = inject(QuotaApiService);
  private readonly state = signal<ModelMigrationCatalogView | null>(null);
  private readonly fetchedAt = signal(0);
  private inFlight: Observable<ModelMigrationCatalogView | null> | null = null;

  readonly catalog = this.state.asReadonly();
  readonly catalogVersion = computed(() => this.state()?.catalogVersion ?? '');
  readonly catalogSource = computed(() => this.state()?.catalogSource ?? '');
  readonly autoApply = computed(() => this.state()?.autoApply ?? true);

  /**
   * The update offered for a pinned model, or null when it is current. Reactive:
   * re-emits once {@link ensure} lands.
   */
  proposalFor(modelId: string | null | undefined): ModelMigrationProposal | null {
    if (!modelId) return null;
    const proposals = this.state()?.proposals;
    if (!proposals) return null;
    return proposals[modelId] ?? proposals[modelId.toLowerCase()] ?? null;
  }

  /**
   * Loads the proposals once per TTL. Safe to call from every consumer's
   * `ngOnInit`; concurrent callers share one request and a failure resolves to
   * "no proposals" so a missing catalog degrades to today's behaviour.
   */
  ensure(): Observable<ModelMigrationCatalogView | null> {
    if (this.state() !== null && Date.now() - this.fetchedAt() < ModelMigrationStore.TTL_MS) {
      return of(this.state());
    }
    if (this.inFlight !== null) return this.inFlight;

    // shareReplay, not the bare pipe: an HttpClient observable is cold, so
    // without it every concurrent consumer would issue its own request.
    this.inFlight = this.api.getModelMigrations().pipe(
      tap((view) => {
        this.state.set(view);
        this.fetchedAt.set(Date.now());
        this.inFlight = null;
      }),
      catchError(() => {
        this.inFlight = null;
        return of(null);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inFlight;
  }

  /** Drops the cache so the next `ensure` re-reads after an applied migration. */
  invalidate(): void {
    this.state.set(null);
    this.fetchedAt.set(0);
    this.inFlight = null;
  }

  setAutoApply(autoApply: boolean): Observable<unknown> {
    const previous = this.state();
    if (previous) this.state.set({ ...previous, autoApply });
    return this.api.setModelMigrationAutoApply(autoApply).pipe(
      catchError((error) => {
        if (previous) this.state.set(previous);
        throw error;
      }),
    );
  }
}
