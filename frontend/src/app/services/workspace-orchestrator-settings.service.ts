import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { Observable } from 'rxjs';

/**
 * AGT-1812 — client for the per-workspace default orchestrator settings (the
 * workspace tier of the two-tier orchestrator config). Talks to the workspace
 * routes added alongside the per-project ones:
 *   GET  /api/workspaces/{id}/settings
 *   PUT  /api/workspaces/{id}/orchestrator-model
 *   PUT  /api/workspaces/{id}/autonomy
 *
 * A null model / thinkingLevel / autonomyLevel means "no workspace default set"
 * — the resolver then falls through to the platform default that the GET
 * response also carries so the UI can render the effective "inherited" value.
 */
export interface WorkspaceOrchestratorSettings {
  orchestratorModel: string | null;
  orchestratorThinkingLevel: string | null;
  autonomyLevel: number | null;
  /** AGT-2716: whether the orchestrator applies a catalog-safe model migration
   * automatically at run admission for a non-explicit model. Always a concrete
   * boolean (server defaults a null store value to true). */
  autoApplyModelMigrations: boolean;
  defaultOrchestratorModel: string;
  defaultAutonomyLevel: number;
}

@Injectable({ providedIn: 'root' })
export class WorkspaceOrchestratorSettingsService {
  private readonly http = inject(HttpClient);

  get(workspaceId: string): Observable<WorkspaceOrchestratorSettings> {
    return this.http.get<WorkspaceOrchestratorSettings>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/settings`,
    );
  }

  /** Set (or clear, with a blank model) the workspace-default orchestrator model. */
  setModel(
    workspaceId: string,
    model: string | null,
    thinkingLevel?: string | null,
  ): Observable<{ orchestratorModel: string | null; orchestratorThinkingLevel: string | null }> {
    return this.http.put<{ orchestratorModel: string | null; orchestratorThinkingLevel: string | null }>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/orchestrator-model`,
      { model, thinkingLevel },
    );
  }

  /** Set (or clear, with null) the workspace-default autonomy level (0..4). */
  setAutonomy(workspaceId: string, level: number | null): Observable<{ autonomyLevel: number | null }> {
    return this.http.put<{ autonomyLevel: number | null }>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/autonomy`,
      { level },
    );
  }

  /** Set (or clear, with null -> restores the on default) the workspace-wide
   * automatic model-migration switch (AGT-2716). */
  setAutoApplyModelMigrations(
    workspaceId: string,
    enabled: boolean | null,
  ): Observable<{ autoApplyModelMigrations: boolean }> {
    return this.http.put<{ autoApplyModelMigrations: boolean }>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/auto-apply-model-migrations`,
      { enabled },
    );
  }
}
