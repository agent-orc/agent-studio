import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { ServerReadinessReport } from '../models/server-readiness.model';

@Injectable({ providedIn: 'root' })
export class ServerReadinessService {
  private readonly http = inject(HttpClient);

  /** The latest report the server computed; never starts Git on the server. */
  read() {
    return this.http.get<ServerReadinessReport>('/api/system/readiness');
  }

  /** Runs every check now. Git facts stay cached for a minute per repository. */
  recheck() {
    return this.http.post<ServerReadinessReport>('/api/system/readiness/refresh', {});
  }
}
