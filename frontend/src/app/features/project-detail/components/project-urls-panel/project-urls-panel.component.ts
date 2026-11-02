import {
  ChangeDetectionStrategy,
  Component,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TaskService } from '../../../../services/task.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { ProjectUrlProbeService, ProjectUrlStatusLabelPipe, type ProjectUrlStatus } from '../../../../services/project-url-probe.service';
import { WorkspaceManagerService } from '../../../shell/state/workspace-manager.service';
import { ProjectUrlRecoveryService } from '../../services/project-url-recovery.service';
import type {
  ProjectUrlStartRule,
  ProjectUrlSuggestion,
  RegistryProjectSummary,
  RegistryProjectUrl,
} from '../../../../models/task.model';

/** Pill shown per row: probe-derived status, upgraded to "building" while a
 *  start/restart is in flight. */
type UrlPill = ProjectUrlStatus | 'building';

/**
 * Project Hub "Project URLs" panel — one flat page to manage a project's
 * watchable URLs. Status strip on top, one row per URL with status pill and
 * Open / Restart / Edit / Remove actions, and an "Add URL" affordance that
 * offers repo-detected suggestions plus a manual form. Adding or removing a
 * URL bumps the registry-changed counter so the Explorer tree reflects it
 * without a reload. Running/offline is projected from the structured backend
 * readiness diagnostic through {@link ProjectUrlProbeService}.
 */
@Component({
  selector: 'app-project-urls-panel',
  standalone: true,
  imports: [FormsModule, StudioIconComponent, TooltipDirective, ProjectUrlStatusLabelPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './project-urls-panel.component.html',
  styleUrl: './project-urls-panel.component.scss',
})
export class ProjectUrlsPanelComponent {
  readonly projectName = input.required<string>();
  readonly quickSetup = input(false);

  /** AGT-2067 — open this URL as an embedded preview tab (primary action).
   *  The Hub view (which owns the tab state) turns it into a `url-preview` tab;
   *  the panel stays presentational and keeps `window.open` as the fallback. */
  readonly openEmbeddedPreview = output<RegistryProjectUrl>();

  private readonly jobService = inject(TaskService);
  private readonly lookup = inject(ProjectLookupService);
  private readonly probe = inject(ProjectUrlProbeService);
  private readonly workspaceManager = inject(WorkspaceManagerService);
  private readonly recovery = inject(ProjectUrlRecoveryService);

  readonly projectId = signal<string | null>(null);
  readonly urls = signal<readonly RegistryProjectUrl[]>([]);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly suggestions = signal<readonly ProjectUrlSuggestion[]>([]);
  readonly addOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  /** url ids with a start/restart in flight (renders a "building" pill). */
  private readonly buildingIds = signal<ReadonlySet<string>>(new Set());

  readonly formLabel = signal('');
  readonly formUrl = signal('');
  readonly formCommand = signal('');
  readonly formCwd = signal('');
  readonly formPort = signal<number | null>(null);
  readonly formHealthUrl = signal('');
  readonly formTimeout = signal(30);
  readonly formStartupTimeout = signal(600);
  readonly formSource = signal('manual');
  readonly testing = signal(false);
  readonly testDiagnostic = signal<import('../../../../models/task.model').ProjectUrlDiagnostic | null>(null);

  readonly runningCount = computed(() =>
    this.urls().filter(u => this.pillFor(u) === 'running').length);
  readonly startableCount = computed(() =>
    this.urls().filter(u => !!u.startRule).length);
  readonly anyBuilding = computed(() => this.buildingIds().size > 0);

  constructor() {
    // Re-resolve whenever the bound project changes.
    effect(() => {
      const name = this.projectName();
      this.refresh(name);
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.jobService.getRegistryWorkspaces().subscribe({
      next: workspaces => {
        this.lookup.setWorkspaces(workspaces ?? []);
        const match = (workspaces ?? [])
          .flatMap(ws => ws.projects)
          .find(p => p.displayName === name);
        this.applyProject(match ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.loadError.set('Could not load project URLs.');
        this.loading.set(false);
      },
    });
  }

  private applyProject(project: RegistryProjectSummary | null): void {
    this.projectId.set(project?.id ?? null);
    this.urls.set([...(project?.urls ?? [])].sort((a, b) => a.sortOrder - b.sortOrder));
    const requested = this.quickSetup() ? this.recovery.takeQuickSetupRequest() : null;
    const requestedUrl = requested && project?.urls.find(url => url.id === requested.urlId);
    if (requestedUrl) {
      this.startEdit(requestedUrl);
      if (requested.suggestion) this.prefillStartRule(requested.suggestion);
      else this.loadSuggestions(true);
      return;
    }
    if (this.quickSetup() && project && project.urls.length === 0 && !this.addOpen()) this.openAdd();
  }

  // ----- status -----

  pillFor(url: RegistryProjectUrl): UrlPill {
    if (this.buildingIds().has(url.id)) return 'building';
    const projectId = this.projectId();
    return projectId ? this.probe.statusFor(projectId, url.id) : 'unknown';
  }

  host(rawUrl: string): string {
    try { return new URL(rawUrl).host; }
    catch { return rawUrl; }
  }

  /** Primary "Open": embed the URL in a preview tab (AGT-2067). */
  openEmbedded(url: RegistryProjectUrl): void {
    this.openEmbeddedPreview.emit(url);
  }

  /** Secondary/fallback "Open": jump to the URL in a real external browser tab. */
  openUrl(url: RegistryProjectUrl): void {
    window.open(url.url, '_blank', 'noopener');
  }

  // ----- build / restart -----

  restart(url: RegistryProjectUrl): void {
    const projId = this.projectId();
    if (!projId || !url.startRule) return;
    this.markBuilding(url.id, true);
    this.jobService.startProjectUrl(projId, url.id).subscribe({
      next: () => this.afterStart(url),
      error: () => this.markBuilding(url.id, false),
    });
  }

  rebuildAll(): void {
    for (const url of this.urls()) {
      if (url.startRule) this.restart(url);
    }
  }

  private afterStart(url: RegistryProjectUrl): void {
    // Give the server a moment to bind its port, then re-probe and clear the
    // building pill.
    setTimeout(() => {
      const projectId = this.projectId();
      if (projectId) this.probe.refresh(projectId, url.id);
      this.markBuilding(url.id, false);
    }, 1200);
  }

  private markBuilding(urlId: string, on: boolean): void {
    const next = new Set(this.buildingIds());
    if (on) next.add(urlId); else next.delete(urlId);
    this.buildingIds.set(next);
  }

  // ----- add / edit / remove -----

  openAdd(): void {
    this.resetForm();
    this.editingId.set(null);
    this.addOpen.set(true);
    this.loadSuggestions();
  }

  startEdit(url: RegistryProjectUrl): void {
    this.editingId.set(url.id);
    this.formLabel.set(url.label);
    this.formUrl.set(url.url);
    this.formCommand.set(url.startRule?.command ?? '');
    this.formCwd.set(url.startRule?.cwd ?? '');
    this.formPort.set(url.startRule?.port ?? null);
    this.formHealthUrl.set(url.startRule?.healthUrl ?? url.url);
    this.formTimeout.set(url.startRule?.readinessTimeoutSeconds ?? 30);
    this.formStartupTimeout.set(url.startRule?.startupTimeoutSeconds ?? 600);
    this.formSource.set(url.startRule?.source ?? 'manual');
    this.suggestions.set([]);
    this.addOpen.set(true);
  }

  closeAdd(): void {
    this.addOpen.set(false);
    this.editingId.set(null);
    this.resetForm();
  }

  private resetForm(): void {
    this.formLabel.set('');
    this.formUrl.set('');
    this.formCommand.set('');
    this.formCwd.set('');
    this.formPort.set(null);
    this.formHealthUrl.set('');
    this.formTimeout.set(30);
    this.formStartupTimeout.set(600);
    this.formSource.set('manual');
    this.testDiagnostic.set(null);
  }

  fillFromSuggestion(s: ProjectUrlSuggestion): void {
    this.formLabel.set(s.label);
    if (s.url) this.formUrl.set(s.url);
    this.formCommand.set(s.command);
    this.formCwd.set(s.cwd ?? '');
    this.formPort.set(s.port);
    this.formHealthUrl.set(s.url ?? this.formUrl());
    this.formSource.set(s.source);
  }

  private prefillStartRule(s: ProjectUrlSuggestion): void {
    this.formCommand.set(s.command);
    this.formCwd.set(s.cwd ?? '');
    this.formPort.set(s.port);
    this.formHealthUrl.set(s.url ?? this.formUrl());
    this.formSource.set(s.source);
  }

  get canSave(): boolean {
    return this.formLabel().trim().length > 0 && this.formUrl().trim().length > 0;
  }

  save(): void {
    const projId = this.projectId();
    if (!projId || !this.canSave || this.saving()) return;
    this.saving.set(true);
    const command = this.formCommand().trim();
    const startRule: ProjectUrlStartRule | null = command
      ? {
          command, cwd: this.formCwd().trim() || null, port: this.formPort(),
          healthUrl: this.formHealthUrl().trim() || null,
          readinessTimeoutSeconds: Math.max(2, Math.min(300, this.formTimeout() || 30)),
          startupTimeoutSeconds: Math.max(2, Math.min(3600, this.formStartupTimeout() || 600)),
          source: this.formSource(),
        }
      : null;
    const body = { label: this.formLabel().trim(), url: this.formUrl().trim(), startRule };
    const editing = this.editingId();
    const request$ = editing
      ? this.jobService.updateProjectUrl(projId, editing, body)
      : this.jobService.addProjectUrl(projId, body);
    request$.subscribe({
      next: updated => {
        this.applyProject(updated);
        this.workspaceManager.notifyRegistryChanged();
        this.saving.set(false);
        this.closeAdd();
      },
      error: () => this.saving.set(false),
    });
  }

  testSetup(): void {
    const projId = this.projectId();
    if (!projId || !this.canSave || this.testing()) return;
    const command = this.formCommand().trim();
    const startRule: ProjectUrlStartRule | null = command ? {
      command, cwd: this.formCwd().trim() || null, port: this.formPort(),
      healthUrl: this.formHealthUrl().trim() || null,
      readinessTimeoutSeconds: Math.max(2, Math.min(300, this.formTimeout() || 30)),
      startupTimeoutSeconds: Math.max(2, Math.min(3600, this.formStartupTimeout() || 600)),
      source: this.formSource(),
    } : null;
    this.testing.set(true);
    this.testDiagnostic.set(null);
    this.jobService.testProjectUrlSetup(projId, {
      label: this.formLabel().trim(), url: this.formUrl().trim(), startRule,
    }).subscribe({
      next: result => { this.testDiagnostic.set(result); this.testing.set(false); },
      error: () => { this.testing.set(false); },
    });
  }

  remove(url: RegistryProjectUrl): void {
    const projId = this.projectId();
    if (!projId) return;
    this.jobService.removeProjectUrl(projId, url.id).subscribe({
      next: updated => {
        this.applyProject(updated);
        this.workspaceManager.notifyRegistryChanged();
      },
    });
  }

  private loadSuggestions(prefillStartRule = false): void {
    const projId = this.projectId();
    if (!projId) { this.suggestions.set([]); return; }
    this.jobService.getProjectUrlSuggestions(projId).subscribe({
      next: list => {
        const values = list ?? [];
        this.suggestions.set(values);
        if (prefillStartRule && values[0]) {
          const configuredPort = this.formPort();
          this.prefillStartRule(values.find(value => value.port === configuredPort) ?? values[0]);
          return;
        }
        if (this.quickSetup() && !this.editingId() && !this.formCommand() && values[0]) {
          this.fillFromSuggestion(values[0]);
        }
      },
      error: () => this.suggestions.set([]),
    });
  }
}
