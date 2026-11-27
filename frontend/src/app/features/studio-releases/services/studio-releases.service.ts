import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

export interface StudioReleaseCard {
  key: string; title: string; type: string | null; href: string; integratedAtUtc: string | null;
}
export interface StudioCardLiveState { state: string; revision: string | null; sinceUtc: string | null; }
export interface StudioReleasesView {
  project: string; runningRevision: string; computedAtUtc: string; detail: string | null;
  pending: StudioReleaseCard[];
  releases: { occurrenceId: string; revision: string; atUtc: string; observed: boolean; timeBasis?: string | null; running: boolean; cards: StudioReleaseCard[] }[];
}
@Injectable({ providedIn: 'root' })
export class StudioReleasesService {
  private readonly http = inject(HttpClient);
  view(project: string) { return this.http.get<StudioReleasesView>(this.path(project)); }
  card(project: string, key: string) {
    return this.http.get<StudioCardLiveState>(`${this.path(project)}/cards/${encodeURIComponent(key)}`);
  }
  private path(project: string) { return `/api/projects/${encodeURIComponent(project)}/studio-releases`; }
}
