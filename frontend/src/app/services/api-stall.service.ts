import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiStallSnapshot } from '../models/api-stall.model';

/** Read client for the API stall detector (`ApiStallEndpoints.cs`). */
@Injectable({ providedIn: 'root' })
export class ApiStallService {
  private readonly http = inject(HttpClient);

  getSnapshot(): Observable<ApiStallSnapshot> {
    return this.http.get<ApiStallSnapshot>('/api/diagnostics/api-stalls');
  }
}
