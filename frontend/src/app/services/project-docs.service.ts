import { Injectable, inject } from '@angular/core';
import {
  HttpClient,
  HttpErrorResponse,
  HttpHeaders,
  HttpParams,
} from '@angular/common/http';
import { catchError, map, of, throwError } from 'rxjs';
import {
  ArchitectureOverview,
  ProjectStyleGuideCatalogue,
  SecurityFileContent,
  SecurityMeta,
  SecurityOverview,
  WikiFileContent,
  WikiFileHistory,
  WikiFileSaveResult,
  WikiFolderOverview,
  WikiGradingAbortResponse,
  WikiGradingRunBody,
  WikiGradingRunStatus,
  WikiGradingStatusResponse,
  WikiHome,
  WikiMaintenanceModelConfig,
  WikiOverview,
  WikiPulse,
  WikiRecentEdits,
  WikiRevisionContent,
  WikiSearchResponse,
  WikiTree,
  WorkbenchCatalogue,
  WorkbenchDocument,
  WorkbenchTaskReferences,
  WorkbenchOverview,
  RecordWorkbenchReviewRequest,
  WorkbenchReviewResult,
} from '../models/project-docs.model';

export interface WikiConditionalResponse<T> {
  modified: boolean;
  etag: string | null;
  body: T | null;
}

/**
 * Reduces a search snippet to text plus literal `<em>` / `</em>` highlight
 * tags. The backend contract already escapes everything except the `<em>`
 * markup; this client-side pass guarantees it: any `<` that does not start an
 * exact `<em>` / `</em>` tag is escaped, so `<em onclick=…>` or any other tag
 * degrades to visible text. Existing entities are left untouched (no double
 * escaping). Only after this pass is the snippet safe for `[innerHTML]`.
 */
export function sanitizeWikiSearchSnippet(snippet: string): string {
  return (snippet ?? '').replace(/<(?!\/?em>)/gi, '&lt;');
}

/**
 * Read/write surface for the project-level Security archive and the
 * Architecture decisions browser. Prototype only — no caching, the
 * components poll on open.
 */
@Injectable({ providedIn: 'root' })
export class ProjectDocsService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  getSecurityOverview(projectName: string) {
    return this.http.get<SecurityOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security`
    );
  }

  getSecurityFile(projectName: string, relPath: string) {
    return this.http.get<SecurityFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/files/${this.encodeRelPath(relPath)}`
    );
  }

  putSecurityFile(projectName: string, relPath: string, content: string) {
    return this.http.put<{ relPath: string; saved: boolean }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/files/${this.encodeRelPath(relPath)}`,
      { content }
    );
  }

  putSecurityMeta(projectName: string, meta: SecurityMeta) {
    return this.http.put<{ saved: boolean }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/security/meta`,
      meta
    );
  }

  getWikiOverview(projectName: string) {
    return this.http.get<WikiOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki`
    );
  }

  /** Technology-aware style guides selected from repository frontmatter. */
  getProjectStyleGuides(projectName: string) {
    return this.http.get<ProjectStyleGuideCatalogue>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/style-guides`
    );
  }

  /** The physical docs/ folder tree (folders + .md/.html files), the wiki nav source. */
  getWikiTree(projectName: string) {
    return this.http.get<WikiTree>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/tree`
    );
  }

  /** Recently-edited wiki pages (page / git author / when), newest first. */
  getWikiRecentEdits(projectName: string, limit = 12) {
    return this.http.get<WikiRecentEdits>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/recent?limit=${limit}`
    );
  }

  /** Conditional recent-edits read used by visible Wiki refresh loops. */
  getWikiRecentEditsVersion(projectName: string, limit = 12, etag: string | null = null) {
    return this.conditionalGet<WikiRecentEdits>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/recent?limit=${limit}`,
      etag,
    );
  }

  /**
   * The generated wiki Pulse landing view (PULSE-1): change feed + inbox +
   * drift grade bar, composed server-side in one call so the landing surface
   * does not multiply the per-doc git lookups.
   */
  getWikiPulse(projectName: string, feedLimit = 12) {
    return this.http.get<WikiPulse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/pulse?feedLimit=${feedLimit}`
    );
  }

  /** Overview of one wiki folder (direct children incl. summaries/sizes). */
  getWikiFolder(projectName: string, relPath: string) {
    return this.http.get<WikiFolderOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/folder/${this.encodeRelPath(relPath)}`
    );
  }

  /**
   * Wiki full-text search; `semantic=true` asks the backend to expand the
   * query. Snippets are sanitised to `<em>`-only markup on the way in so the
   * result list may bind them via `[innerHTML]`.
   */
  searchWiki(projectName: string, query: string, options: { semantic?: boolean; limit?: number } = {}) {
    let params = new HttpParams().set('q', query);
    if (options.semantic) params = params.set('semantic', 'true');
    if (options.limit != null) params = params.set('limit', String(options.limit));
    return this.http.get<WikiSearchResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/search`,
      { params }
    ).pipe(map(response => ({
      ...response,
      expandedTerms: response.expandedTerms ?? [],
      results: (response.results ?? []).map(result => ({
        ...result,
        snippet: sanitizeWikiSearchSnippet(result.snippet),
      })),
    })));
  }

  /** Curated entry links ("Einstiege") for the wiki landing surface. */
  getWikiHome(projectName: string) {
    return this.http.get<WikiHome>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/home`
    );
  }

  /** Add/update/move or remove one shared, versioned Wiki Overview pin. */
  setWikiHomePin(
    projectName: string,
    relPath: string,
    pin: {
      pinned: boolean;
      sectionTitle?: string | null;
      label?: string | null;
      note?: string | null;
    },
  ) {
    return this.http.put<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/home/pins/${this.encodeRelPath(relPath)}`,
      pin,
    );
  }

  getWorkbenches(projectName: string, history = false) {
    return this.http.get<WorkbenchCatalogue>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/workbenches`,
      { params: history ? { history: 'true' } : {} },
    );
  }

  /** Shared workspace-wide/project-scoped Dossier queue projection. */
  getWorkbenchOverview(projectName: string | null = null) {
    return this.http.get<WorkbenchOverview>(`${this.baseUrl}/workbenches`, {
      params: projectName ? { project: projectName } : {},
    });
  }

  getWorkbench(projectName: string, id: string) {
    return this.http.get<WorkbenchDocument>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/workbenches/${encodeURIComponent(id)}`,
    );
  }

  recordWorkbenchReview(projectName: string, id: string, review: RecordWorkbenchReviewRequest) {
    return this.http.put<WorkbenchReviewResult>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/workbenches/${encodeURIComponent(id)}/review`,
      review,
    );
  }

  getWorkbenchReferences(projectName: string, key: string) {
    return this.http.get<WorkbenchTaskReferences>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/workbenches/${encodeURIComponent(key)}/references`,
    );
  }

  getWikiFile(projectName: string, relPath: string) {
    return this.http.get<WikiFileContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`
    );
  }

  putWikiFile(projectName: string, relPath: string, content: string) {
    return this.http.put<WikiFileSaveResult>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`,
      { content }
    );
  }

  /** Update page lifecycle metadata without moving or deleting the source. */
  setWikiClassification(projectName: string, relPath: string, status: 'archived' | 'aktuell') {
    return this.http.put<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/classification/${this.encodeRelPath(relPath)}`,
      { status },
    );
  }

  /** Absolute API URL for a wiki image/diagram asset (used by the image resolver). */
  wikiAssetUrl(projectName: string, relPath: string): string {
    return `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/assets/${this.encodeRelPath(relPath)}`;
  }

  /** Per-document provenance (model/when/why) + git history, newest first. */
  getWikiFileHistory(projectName: string, relPath: string) {
    return this.http.get<WikiFileHistory>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/history/${this.encodeRelPath(relPath)}`
    );
  }

  /** Conditional per-file history read whose ETag also covers live file edits. */
  getWikiFileHistoryVersion(projectName: string, relPath: string, etag: string | null = null) {
    return this.conditionalGet<WikiFileHistory>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/history/${this.encodeRelPath(relPath)}`,
      etag,
    );
  }

  /** Content of a wiki doc as it existed at an earlier commit (old-revision view). */
  getWikiRevision(projectName: string, sha: string, relPath: string) {
    return this.http.get<WikiRevisionContent>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/revisions/${encodeURIComponent(sha)}/${this.encodeRelPath(relPath)}`
    );
  }

  /** Create a new wiki page (.md/.html); the server commits it into the repo. */
  createWikiPage(projectName: string, relPath: string, content?: string) {
    return this.http.post<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/pages`,
      { relPath, content: content ?? null }
    );
  }

  /** Create a new wiki folder; the server seeds a .gitkeep and commits it. */
  createWikiFolder(projectName: string, relPath: string) {
    return this.http.post<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/folders`,
      { relPath }
    );
  }

  /** Move/rename a wiki node (file or folder) via git mv + commit. */
  moveWikiNode(projectName: string, fromRelPath: string, toRelPath: string) {
    return this.http.post<{ from: string; to: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/move`,
      { fromRelPath, toRelPath }
    );
  }

  /**
   * Persist the display order of the category folders under one parent
   * ("" = docs root). Stored server-side beside the other wiki metadata
   * (docs/app/config/wiki-order.json) and committed like every other wiki mutation.
   */
  setWikiFolderOrder(projectName: string, parentRelPath: string, orderedNames: string[]) {
    return this.http.put<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/folder-order`,
      { parentRelPath, orderedNames }
    );
  }

  /** Persist the display order of documents under one wiki folder. */
  setWikiFileOrder(projectName: string, parentRelPath: string, orderedNames: string[]) {
    return this.http.put<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/file-order`,
      { parentRelPath, orderedNames }
    );
  }

  /** Delete a wiki node (file or folder) via git rm + commit. */
  deleteWikiNode(projectName: string, relPath: string) {
    return this.http.delete<{ relPath: string; sha: string }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/files/${this.encodeRelPath(relPath)}`
    );
  }

  // ---- Wiki grading maintenance run (AGT-2051) ----

  /** Start a global grading pass. Fields default from the maintenance model. */
  startWikiGrading(projectName: string, body: WikiGradingRunBody = {}) {
    return this.http.post<WikiGradingRunStatus>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/run`,
      body
    );
  }

  /** Poll the latest run status (`status` is null until the first run starts). */
  getWikiGradingStatus(projectName: string) {
    return this.http.get<WikiGradingStatusResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/status`
    );
  }

  /** Request cancellation of an in-flight grading run. */
  abortWikiGrading(projectName: string) {
    return this.http.post<WikiGradingAbortResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/wiki/grading/abort`,
      {}
    );
  }

  /** Read the workspace maintenance-model default (CLI-management area). */
  getMaintenanceModel() {
    return this.http.get<WikiMaintenanceModelConfig>(`${this.baseUrl}/cli/maintenance-model`);
  }

  /** Write the workspace maintenance-model default. */
  setMaintenanceModel(config: Partial<WikiMaintenanceModelConfig>) {
    return this.http.put<WikiMaintenanceModelConfig>(`${this.baseUrl}/cli/maintenance-model`, config);
  }

  getArchitectureOverview(projectName: string) {
    return this.http.get<ArchitectureOverview>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/architecture`
    );
  }

  private encodeRelPath(relPath: string): string {
    return relPath.split('/').map(encodeURIComponent).join('/');
  }

  private conditionalGet<T>(url: string, etag: string | null) {
    const headers = etag
      ? new HttpHeaders({ 'If-None-Match': etag })
      : undefined;
    return this.http.get<T>(url, { headers, observe: 'response' }).pipe(
      map(response => ({
        modified: true,
        etag: response.headers.get('ETag'),
        body: response.body,
      }) satisfies WikiConditionalResponse<T>),
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 304) {
          return of({
            modified: false,
            etag: error.headers.get('ETag') ?? etag,
            body: null,
          } satisfies WikiConditionalResponse<T>);
        }
        return throwError(() => error);
      }),
    );
  }
}
