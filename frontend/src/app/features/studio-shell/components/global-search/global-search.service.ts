import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';

export type SearchDomain = 'tasks' | 'commits' | 'files' | 'dossiers';

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
  /** Document reference key of a Dossier hit, for example `AGT-W15`. */
  dossierKey?: string;
  /** Dossier viewer route: this id plus `projectName`, never a repository path. */
  dossierId?: string;
  /** Dossier summary, shown as the result excerpt. */
  summary?: string;
}

export interface GlobalSearchResponse {
  query: string;
  tasks: GlobalSearchItem[];
  commits: GlobalSearchItem[];
  files: GlobalSearchItem[];
  dossiers: GlobalSearchItem[];
  errors: Record<string, string>;
  durationMs: number;
}

@Injectable({ providedIn: 'root' })
export class GlobalSearchService {
  private readonly http = inject(HttpClient);

  search(query: string) {
    const params = new HttpParams()
      .set('q', query)
      .set('domains', 'tasks,commits,files,dossiers')
      .set('limit', 20);
    return this.http.get<GlobalSearchResponse>('/api/search', { params });
  }
}
