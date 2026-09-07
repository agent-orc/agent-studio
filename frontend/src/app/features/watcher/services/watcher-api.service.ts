import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type {
  WatcherCase,
  WatcherContingentSnapshot,
  WatcherDecisionRequest,
  WatcherProposal,
  WatcherStatus,
  WatcherSuppressionView,
} from '../models/watcher.model';

/**
 * HTTP client for `/api/watcher`. The Watcher's read surface plus the one
 * write an operator makes: the decision on a proposal. There is deliberately
 * no client call that creates a case or a proposal; both are outputs of the
 * sweep.
 */
@Injectable({ providedIn: 'root' })
export class WatcherApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/watcher';

  status() {
    return this.http.get<WatcherStatus>(`${this.baseUrl}/status`);
  }

  contingent() {
    return this.http.get<WatcherContingentSnapshot>(`${this.baseUrl}/contingent`);
  }

  cases() {
    return this.http.get<{ cases: WatcherCase[] }>(`${this.baseUrl}/cases`);
  }

  /** Pass `pending` to get only the proposals still awaiting an answer. */
  proposals(decision?: string) {
    const query = decision ? `?decision=${encodeURIComponent(decision)}` : '';
    return this.http.get<{ proposals: WatcherProposal[] }>(`${this.baseUrl}/proposals${query}`);
  }

  proposal(proposalId: string) {
    return this.http.get<WatcherProposal>(`${this.baseUrl}/proposals/${encodeURIComponent(proposalId)}`);
  }

  /**
   * Record the operator answer. Approve and edit move the proposed card to
   * Ready with the recommended model; merge and reject leave the run queue
   * untouched.
   */
  decide(proposalId: string, request: WatcherDecisionRequest) {
    return this.http.post<WatcherProposal>(
      `${this.baseUrl}/proposals/${encodeURIComponent(proposalId)}/decision`,
      request
    );
  }

  suppressions() {
    return this.http.get<{ suppressions: WatcherSuppressionView[] }>(`${this.baseUrl}/suppressions`);
  }
}
