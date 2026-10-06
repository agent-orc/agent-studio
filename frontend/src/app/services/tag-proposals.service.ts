import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, tap } from 'rxjs';

/** Change this one flag when AGT-2804's proposal API is integrated. */
export const USE_TAG_PROPOSAL_MOCK = true;
export type TagProposalSubjectKind = 'task' | 'dossier' | 'wiki';
export interface TagProposal {
  id: string;
  projectName: string;
  subjectKind: TagProposalSubjectKind;
  subjectId: string;
  tagIds: string[];
  confidence: number;
  state: 'pending' | 'accepted' | 'rejected';
}
export interface TagProposalDecision { choice: 'accept' | 'reject' }

@Injectable({ providedIn: 'root' })
export class TagProposalsService {
  private readonly http = inject(HttpClient);
  readonly proposals = signal<TagProposal[]>([]);
  private readonly loaded = new Set<string>();

  load(projectName: string): void {
    if (this.loaded.has(projectName)) return;
    this.loaded.add(projectName);
    if (USE_TAG_PROPOSAL_MOCK) {
      try {
        const fixture = JSON.parse(localStorage.getItem('tagProposalsMock') ?? '[]') as TagProposal[];
        if (fixture.length) this.proposals.update(items => [...items.filter(item => item.projectName !== projectName),
          ...fixture.filter(item => item.projectName === projectName)]);
      } catch { /* An invalid local fixture produces an empty proposal set. */ }
      return;
    }
    this.http.get<{ items: TagProposal[] }>(`/api/projects/${encodeURIComponent(projectName)}/tag-proposals`)
      .subscribe({ next: response => this.proposals.update(items => [
        ...items.filter(item => item.projectName !== projectName), ...response.items,
      ]), error: () => this.loaded.delete(projectName) });
  }

  decide(projectName: string, id: string, choice: TagProposalDecision['choice']): Observable<TagProposal> {
    const proposal = this.proposals().find(item => item.projectName === projectName && item.id === id);
    if (USE_TAG_PROPOSAL_MOCK) {
      if (!proposal) throw new Error(`Unknown tag proposal ${id}`);
      const updated: TagProposal = { ...proposal, state: choice === 'accept' ? 'accepted' : 'rejected' };
      return of(updated).pipe(
        tap(updated => this.replace(updated)));
    }
    return this.http.post<TagProposal>(
      `/api/projects/${encodeURIComponent(projectName)}/tag-proposals/${encodeURIComponent(id)}/decision`,
      { choice } satisfies TagProposalDecision,
    ).pipe(tap(updated => this.replace(updated)));
  }

  /** Test fixture injection for the frontend mock. */
  seedMock(items: TagProposal[]): void { if (USE_TAG_PROPOSAL_MOCK) this.proposals.set(items); }

  private replace(updated: TagProposal): void {
    this.proposals.update(items => items.map(item =>
      item.projectName === updated.projectName && item.id === updated.id ? updated : item));
  }
}
