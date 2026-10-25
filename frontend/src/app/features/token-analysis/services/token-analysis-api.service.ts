import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { TokenAnalysisCardDetail, TokenAnalysisResponse } from '../models/token-analysis.model';

/** HTTP client of the card-centric token analysis (AGT-3267). */
@Injectable({ providedIn: 'root' })
export class TokenAnalysisApiService {
  private readonly http = inject(HttpClient);

  /** Cards with usage in the last `days` UTC days; `project` narrows to one project. */
  getAnalysis(days: number, project: string | null) {
    let params = new HttpParams().set('days', String(days));
    if (project) params = params.set('project', project);
    return this.http.get<TokenAnalysisResponse>('/api/workspace/tokens/analysis', { params });
  }

  /** Drill-down timeline of one card over its whole history. */
  getCardDetail(project: string, jobId: string) {
    return this.http.get<TokenAnalysisCardDetail>(
      `/api/projects/${encodeURIComponent(project)}/token-usage/analysis/cards/${encodeURIComponent(jobId)}`,
    );
  }
}
