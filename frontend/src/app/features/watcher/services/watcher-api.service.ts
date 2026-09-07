import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import type {
  WatcherCase,
  WatcherProposal,
  WatcherProposalDecisionRequest,
  WatcherStatusResponse,
} from '../models/watcher.model';

/**
 * Review-mode API client for the Global Orchestrator Watcher
 * (orchestrator-waechter dossier §10.4). Wraps `/api/watcher`.
 */
@Injectable({ providedIn: 'root' })
export class WatcherApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/watcher';

  status(): Observable<WatcherStatusResponse> {
    return this.http.get<WatcherStatusResponse>(`${this.baseUrl}/status`);
  }

  cases(state?: string): Observable<{ cases: WatcherCase[] }> {
    const query = state ? `?state=${encodeURIComponent(state)}` : '';
    return this.http.get<{ cases: WatcherCase[] }>(`${this.baseUrl}/cases${query}`);
  }

  proposals(pendingOnly = false): Observable<{ proposals: WatcherProposal[] }> {
    const query = pendingOnly ? '?pending=true' : '';
    return this.http.get<{ proposals: WatcherProposal[] }>(`${this.baseUrl}/proposals${query}`);
  }

  decide(proposalId: string, request: WatcherProposalDecisionRequest): Observable<{ proposal: WatcherProposal }> {
    return this.http.post<{ proposal: WatcherProposal }>(
      `${this.baseUrl}/proposals/${encodeURIComponent(proposalId)}/decision`,
      request
    );
  }
}
