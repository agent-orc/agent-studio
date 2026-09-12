import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ProjectDocsService } from '../../../services/project-docs.service';
import type { WorkbenchCatalogue, WikiSearchResponse } from '../../../models/project-docs.model';
import {
  contextSourceId,
  type OrchestratorContextSourceOption,
} from '../models/orchestrator-context-source.model';
import type { OrchestratorContextReference } from '../models/orchestrator.model';

interface KnownSourceItem {
  domain: 'tasks' | 'commits' | 'files';
  projectName: string;
  title: string;
  subtitle: string;
  taskKey?: string;
  lane?: string;
  sha?: string;
  path?: string;
  isWiki?: boolean;
}

/** Repository-bound domains only (dev-seat `GET /api/search/repository`). */
interface RepositorySearchResponse {
  commits: KnownSourceItem[];
  files: KnownSourceItem[];
}

/** One Task-Server-owned task match (`GET /api/search`, AGT-2758). */
interface StudioSearchTaskItem {
  taskId: string;
  taskKey: string;
  title: string;
  projectId: string;
  state: string;
}

interface StudioSearchResponse {
  query: string;
  tasks: StudioSearchTaskItem[];
}

export interface OrchestratorContextSourceSearchResult {
  tasks: OrchestratorContextSourceOption[];
  wiki: OrchestratorContextSourceOption[];
  files: OrchestratorContextSourceOption[];
  commits: OrchestratorContextSourceOption[];
  degraded: boolean;
}

const EMPTY_SEARCH: OrchestratorContextSourceSearchResult = {
  tasks: [], wiki: [], files: [], commits: [], degraded: false,
};

@Injectable({ providedIn: 'root' })
export class OrchestratorContextSourceService {
  private readonly http = inject(HttpClient);
  private readonly docs = inject(ProjectDocsService);

  search(project: string, query: string) {
    const taskParams = new HttpParams().set('q', query).set('project', project).set('limit', 12);
    const repositoryParams = new HttpParams().set('q', query).set('domains', 'commits,files').set('limit', 12);
    return forkJoin({
      // Task matches are Task-Server-owned; commit/file matches stay on the
      // dev-seat repository search (AGT-2758 splits the legacy mixed
      // `GET /api/search` contract - see docs/studio-route-ownership).
      known: this.http.get<StudioSearchResponse>('/api/search', { params: taskParams })
        .pipe(catchError(() => of<StudioSearchResponse>({ query, tasks: [] }))),
      repository: this.http.get<RepositorySearchResponse>('/api/search/repository', { params: repositoryParams })
        .pipe(catchError(() => of<RepositorySearchResponse>({ commits: [], files: [] }))),
      wiki: this.docs.searchWiki(project, query, { limit: 12 })
        .pipe(catchError(() => of<WikiSearchResponse | null>(null))),
      workbenches: this.docs.getWorkbenches(project)
        .pipe(catchError(() => of<WorkbenchCatalogue | null>(null))),
    }).pipe(map(({ known, repository, wiki, workbenches }) => {
      const sameProject = (item: KnownSourceItem) => item.projectName === project;
      const tasks = known.tasks.map(item => this.option(
        'tasks', item.title, `${item.taskKey} · ${item.state ?? 'Task'}`,
        { kind: 'task', reference: item.taskKey, projectId: project }, 900,
        item.taskKey));
      const files = repository.files.filter(sameProject).map(item => this.option(
        item.isWiki ? 'wiki' : 'files', item.title, item.path ?? item.subtitle,
        item.isWiki
          ? { kind: 'page', reference: `page:${project}/${(item.path ?? '').replace(/^docs\//i, '')}`, projectId: project }
          : { kind: 'repository-file', reference: item.path ?? item.subtitle, projectId: project },
        item.isWiki ? 1_200 : 700));
      const commits = repository.commits.filter(sameProject).map(item => this.option(
        'commits', item.title, item.sha?.slice(0, 8) ?? item.subtitle,
        { kind: 'commit', reference: `commit:${project}/${item.sha ?? item.subtitle}`, projectId: project }, 1_400));
      const wikiPages = (wiki?.results ?? []).map(item => this.option(
        'wiki', item.title, item.relPath,
        { kind: 'page', reference: `page:${project}/${item.relPath}`, projectId: project }, 1_200));
      const q = query.toLocaleLowerCase();
      const benches = (workbenches?.items ?? [])
        .filter(item => `${item.key} ${item.title} ${item.summary} ${item.status}`.toLocaleLowerCase().includes(q))
        .slice(0, 8)
        .map(item => this.option(
          'wiki', item.title, `Dossier · ${item.status}${item.phase ? ` · ${item.phase}` : ''}`,
          { kind: 'page', reference: `page:${project}/${item.entryPath}`, projectId: project }, 1_200,
          item.key ?? undefined));
      return {
        tasks,
        wiki: this.unique([...benches, ...wikiPages, ...files.filter(item => item.category === 'wiki')]),
        files: files.filter(item => item.category === 'files'),
        commits,
        degraded: wiki === null || workbenches === null,
      } satisfies OrchestratorContextSourceSearchResult;
    }), catchError(() => of({ ...EMPTY_SEARCH, degraded: true })));
  }

  private option(
    category: OrchestratorContextSourceOption['category'],
    label: string,
    detail: string,
    reference: OrchestratorContextReference,
    estimateTokens: number,
    key?: string,
  ): OrchestratorContextSourceOption {
    return {
      id: contextSourceId(reference),
      category,
      ...(key ? { key } : {}),
      label,
      detail,
      reference,
      estimateTokens,
    };
  }

  private unique(items: OrchestratorContextSourceOption[]): OrchestratorContextSourceOption[] {
    return [...new Map(items.map(item => [item.id, item])).values()];
  }
}
