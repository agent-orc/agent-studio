import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, of, shareReplay, catchError, map, tap } from 'rxjs';
import type { WorkbenchOverview } from '../models/project-docs.model';
import { EMPTY_DOSSIER_INDEX, buildDossierIndex, type DossierIndex } from './dossier-reference.util';

/**
 * The Dossier catalogue every reference surface resolves against.
 *
 * One workspace-wide read of the existing overview projection (it already
 * carries id, key, title, status, project, and entry path, history included)
 * backs the resolver for prose, review documents, prompts, activity, the Wiki,
 * and search. Surfaces never fetch per mention.
 */
@Injectable({ providedIn: 'root' })
export class DossierCatalogueService {
  private readonly http = inject(HttpClient);
  private request: Observable<DossierIndex> | null = null;
  readonly index = signal<DossierIndex>(EMPTY_DOSSIER_INDEX);

  /** Load once per session; every later caller shares the cached index. */
  ensureLoaded(): Observable<DossierIndex> {
    this.request ??= this.http.get<WorkbenchOverview>('/api/workbenches').pipe(
      map(overview => buildDossierIndex(overview?.items ?? [])),
      // A denied or failed read leaves every mention as plain text, which is
      // exactly the pre-chip rendering; it must never break the document.
      catchError(() => of(EMPTY_DOSSIER_INDEX)),
      tap(index => this.index.set(index)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.request;
  }
}
