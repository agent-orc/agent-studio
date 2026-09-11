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

interface KnownSourceResponse {
  tasks: KnownSourceItem[];
  commits: KnownSourceItem[];
  files: KnownSourceItem[];
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
    // Split per AGT-2758: task results are Task-Server-owned (forwarded to
    // GET /api/v1/studio/search); commit/file results stay dev-seat-local
    // (GET /api/search/repository, a local git checkout read). The two calls
    // are merged into the same KnownSourceResponse shape callers already use.
    const taskParams = new HttpParams().set('q', query).set('domains', 'tasks').set('limit', 12);
    const repoParams = new HttpParams().set('q', query).set('domains', 'commits,files').set('limit', 12);
    return forkJoin({
      knownTasks: this.http.get<Pick<KnownSourceResponse, 'tasks'>>('/api/search', { params: taskParams })
        .pipe(catchError(() => of<Pick<KnownSourceResponse, 'tasks'>>({ tasks: [] }))),
      knownRepository: this.http.get<Pick<KnownSourceResponse, 'commits' | 'files'>>(
        '/api/search/repository', { params: repoParams },
      ).pipe(catchError(() => of<Pick<KnownSourceResponse, 'commits' | 'files'>>({ commits: [], files: [] }))),
      wiki: this.docs.searchWiki(project, query, { limit: 12 })
        .pipe(catchError(() => of<WikiSearchResponse | null>(null))),
      workbenches: this.docs.getWorkbenches(project)
        .pipe(catchError(() => of<WorkbenchCatalogue | null>(null))),
    }).pipe(map(({ knownTasks, knownRepository, wiki, workbenches }) => {
      const known: KnownSourceResponse = {
        tasks: knownTasks.tasks ?? [],
        commits: knownRepository.commits ?? [],
        files: knownRepository.files ?? [],
      };
      const sameProject = (item: KnownSourceItem) => item.projectName === project;
      const tasks = known.tasks.filter(sameProject).map(item => this.option(
        'tasks', item.title, `${item.taskKey ?? item.subtitle} · ${item.lane ?? 'Task'}`,
        { kind: 'task', reference: item.taskKey ?? item.subtitle, projectId: project }, 900,
        item.taskKey));
      const files = known.files.filter(sameProject).map(item => this.option(
        item.isWiki ? 'wiki' : 'files', item.title, item.path ?? item.subtitle,
        item.isWiki
          ? { kind: 'page', reference: `page:${project}/${(item.path ?? '').replace(/^docs\//i, '')}`, projectId: project }
          : { kind: 'repository-file', reference: item.path ?? item.subtitle, projectId: project },
        item.isWiki ? 1_200 : 700));
      const commits = known.commits.filter(sameProject).map(item => this.option(
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
