import { Injectable, inject, signal } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { CliType, CLI_TYPES, ComponentRoutingRequest, ComponentRoutingResolution, PromoteToCodingResponse, TaskMode, TaskState, WatchPathEntry } from '../../../models/task.model';
import type { CliModelInfo } from '../../../features/cli';
import type { PendingAttachment } from '../components/create-task-dialog/create-task-dialog.component';
import { TaskService } from '../../../services/task.service';
import { CliCatalogStore } from '../../cli';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { sessionFetch } from '../../../services/session-fetch';
import { PageTaskRequest, pageContextKey } from '../../../models/page-context.model';
import { QuotaApiService, type ModelRoutingRecommendation } from '../../quota';

/**
 * Cycle 10a board-feature service: owns every field the create-job
 * dialog binds, plus the prefilled entry points the shell
 * used to host inline (default open, security-follow-up,
 * uxui-follow-up, and planning-task promotion).
 *
 * Lifted out of `app.ts` per ADR-0034. The shell now just renders
 * `<app-create-job-dialog>` against this service's bound fields and
 * delegates open / cancel / submit to it. The shell still passes
 * `watchPaths` + `activeProjects` into the open methods because those
 * live on the shell (workspace-scoped); the service stays free of
 * cross-feature coupling.
 *
 * Refresh-after-submit is exposed as the `submitted$` event the shell
 * subscribes to; the service does not call `TaskService.refresh` itself
 * because the shell owns that orchestration concern.
 */
@Injectable({ providedIn: 'root' })
export class CreateTaskFormService {
  private readonly jobService = inject(TaskService);
  private readonly catalogStore = inject(CliCatalogStore);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly routingPolicy = inject(QuotaApiService);

  readonly visible = signal(false);
  readonly routing = signal<ComponentRoutingResolution | null>(null);
  readonly routingPending = signal(false);
  private routingRequest: ComponentRoutingRequest | null = null;

  // Plain mutable fields — these are [(ngModel)]-bound by the dialog and
  // mutated directly. Keeping them as fields (not signals) preserves the
  // previous template ergonomics.
  newTitle = '';
  newWatchPath = '';
  newAgent: CliType = 'claude';
  newPrompt = '';
  newTargetState: string = TaskState.Preparation;
  newTaskType = 'chore';
  newTags: string[] = [];
  /** Execution mode (coding | planning | research). Defaults to coding. */
  newMode: TaskMode = 'coding';
  /** Web access. Default-by-mode lives in the picker (research = on, else off). */
  newAllowWebAccess = false;
  newCliType: CliType = readDefaultCliPref();
  newModel: string = readDefaultModelPref(readDefaultCliPref());
  newThinkingLevel: string | null = readDefaultThinkingLevelPref(readDefaultCliPref());
  modelSelectionExplicit = false;
  newAttachments: PendingAttachment[] = [];

  /** Allowed manual-create lanes (everything before 3-progress). */
  static readonly ALLOWED_TARGET_STATES = [TaskState.Backlog, TaskState.Preparation, TaskState.Ready] as const;

  readonly availableModels = signal<CliModelInfo[]>([]);
  readonly policySuggestion = signal<ModelRoutingRecommendation | null>(null);
  private suggestionRequest = 0;

  /** Fired after a successful createJob; shell listens to refresh the board. */
  private readonly submittedSubject = new Subject<{ jobId: string }>();
  readonly submitted$: Observable<{ jobId: string }> = this.submittedSubject.asObservable();

  /**
   * Whether a "+ Add task" button is allowed in the named lane. Lanes
   * past 2-ready don't accept manual creation — they fill via
   * orchestrator runs / triage.
   */
  canAddTaskToGroup(state: string): boolean {
    return state === TaskState.Backlog || state === TaskState.Preparation || state === TaskState.Ready;
  }

  // ---------- open entry points ----------

  /**
   * Default open from the toolbar `+ New` button or a lane "Add task"
   * affordance. Picks the watch path from the user's last-used + active
   * projects. `targetState` is honoured when it names one of the manual
   * create lanes (`0-backlog` / `1-preparation` / `2-ready`); anything
   * else falls back to `1-preparation`.
   */
  open(opts: {
    watchPaths: readonly WatchPathEntry[];
    activeProjects: ReadonlySet<string>;
    targetState?: string;
  }): void {
    const requested = opts.targetState ?? '';
    this.newTargetState = (CreateTaskFormService.ALLOWED_TARGET_STATES as readonly string[]).includes(requested)
      ? requested
      : TaskState.Preparation;
    this.newWatchPath = pickCreateWatchPath(opts.watchPaths, opts.activeProjects);
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
  }

  /**
   * Project-Security panel "Create follow-up" action. Pre-fills the
   * dialog with a prompt body the panel composed from a recent review.
   */
  openSecurityFollowUp(
    event: { projectName: string; prefill: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = TaskState.Preparation;
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = `Security follow-up (${event.projectName})`;
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
  }

  /**
   * Project-UXUI panel "Create follow-up" / per-row "Task" action.
   * Pre-fills the dialog with a prompt body composed from a council
   * note or design overview, plus a panel-supplied title.
   */
  openUxuiFollowUp(
    event: { projectName: string; prefill: string; title: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = TaskState.Preparation;
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.prefill;
    this.newTitle = event.title;
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
  }

  /**
   * Shared page action-bar entry point. The durable task prompt carries the
   * canonical page reference plus a bounded excerpt so the card preserves its
   * origin even after the operator closes the page.
   */
  openPageTask(
    request: PageTaskRequest,
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const page = request.context;
    const watchEntry = watchPaths.find((wp) => wp.name === page.projectName);
    if (!watchEntry) return;

    const instruction = request.intent === 'build-feature'
      ? 'Turn the page proposal into a production feature. Reconcile it with current product and architecture contracts, then implement and verify the smallest complete slice.'
      : request.intent === 'create-follow-up'
        ? 'Investigate the incident or history evidence, identify the remaining prevention gap, and implement a verified follow-up.'
        : 'Use this page as the source context for the requested project change. Verify the current implementation before changing it.';
    const titlePrefix = request.intent === 'build-feature'
      ? 'Build feature'
      : request.intent === 'create-follow-up'
        ? 'Page follow-up'
        : 'Task from page';

    this.newTargetState = TaskState.Preparation;
    this.newWatchPath = watchEntry.path;
    this.newTitle = `${titlePrefix}: ${page.title}`;
    this.newPrompt = [
      '# Page-backed task',
      '',
      `Source page: \`${pageContextKey(page)}\``,
      `Page type: ${page.pageType}`,
      `Project: ${page.projectName}`,
      '',
      '## Page excerpt',
      '',
      page.excerpt || '(No excerpt available.)',
      '',
      '## Requested outcome',
      '',
      instruction,
    ].join('\n');
    this.newMode = 'coding';
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
  }

  /**
   * Orchestrator-draft "Create task from this draft" action. Title is
   * derived from the prompt's first non-empty line.
   */
  openOrchestratorDraftFollowUp(
    event: { projectName: string; promptText: string },
    watchPaths: readonly WatchPathEntry[],
  ): void {
    const watchEntry = watchPaths.find((wp) => wp.name === event.projectName);
    if (!watchEntry) return;
    this.newTargetState = TaskState.Preparation;
    this.newWatchPath = watchEntry.path;
    this.newPrompt = event.promptText;
    this.newTitle = deriveDraftTitle(event.promptText);
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
    this.routingPending.set(true);
    this.routingRequest = {
      observedSurface: 'Agent Studio Orchestrator chat',
      component: event.promptText,
      navigationProjectId: event.projectName,
    };
    this.jobService.resolveComponentRouting(this.routingRequest).subscribe({
      next: (route) => {
        this.routing.set(route);
        if (route.primaryProject && !route.requiresQuestion) {
          this.jobService.getRegistryWorkspaces({ includeArchived: true }).subscribe({
            next: (workspaces) => {
              const destination = workspaces.flatMap(workspace => workspace.projects)
                .find(project => project.id === route.storageProjectId);
              if (destination) this.newWatchPath = destination.storageLocation;
              this.routingPending.set(false);
            },
            error: () => this.routingPending.set(false),
          });
        } else {
          this.routingPending.set(false);
        }
      },
      error: () => {
        this.routingPending.set(false);
        this.routing.set(null);
      },
    });
  }

  /**
   * "Promote to coding task" from a finished planning task's Overview. The
   * caller (overview-pane) has already fetched the pre-fill payload from
   * `GET /promote-to-coding` and turned each copyable image into a
   * `PendingAttachment` (blob -> File). We seed the dialog with that draft —
   * title, prompt body, same project, mode=coding — and surface the images as
   * already-attached chips. On Save the existing attachment-upload pipeline
   * copies them byte-for-byte into the new task. See
   * docs/concepts/planning-research-task-kinds-2026-05.md.
   */
  openPromotePlanning(payload: PromoteToCodingResponse, attachments: PendingAttachment[]): void {
    this.newTitle = payload.title;
    this.newPrompt = payload.promptMarkdown;
    this.newWatchPath = payload.watchPath;
    this.newMode = 'coding';
    this.newTargetState = (CreateTaskFormService.ALLOWED_TARGET_STATES as readonly string[]).includes(payload.targetState)
      ? payload.targetState
      : TaskState.Preparation;
    // Revoke any previews carried over from a prior open before replacing.
    for (const att of this.newAttachments) URL.revokeObjectURL(att.previewUrl);
    this.newAttachments = attachments;
    this.loadCreateModels(this.newCliType);
    this.refreshPolicySuggestion();
    this.visible.set(true);
  }

  // ---------- ngModel-driven mutators ----------

  onCreateCliTypeChange(t: CliType): void {
    if (this.newCliType === t) return;
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
    this.newThinkingLevel = readDefaultThinkingLevelPref(t);
    this.loadCreateModels(t);
    this.refreshPolicySuggestion();
  }

  markModelSelectionExplicit(): void {
    this.modelSelectionExplicit = true;
  }

  onTaskTypeChange(taskType: string): void {
    this.newTaskType = taskType;
    this.refreshPolicySuggestion();
  }

  usePolicySelection(): void {
    this.modelSelectionExplicit = false;
    this.applyPolicySuggestion();
  }

  /** Mirrors a global "default model for CLI X" change into the form when relevant. */
  onDefaultModelChange(ev: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    if (ev.cliType === this.newCliType) {
      this.newModel = ev.model;
      this.newThinkingLevel = ev.thinkingLevel;
    }
  }

  /** Apply user's stored CLI default before showing the dialog. */
  applyStoredCliDefault(): void {
    const t = readDefaultCliPref();
    this.newCliType = t;
    this.newModel = readDefaultModelPref(t);
    this.newThinkingLevel = readDefaultThinkingLevelPref(t);
  }

  // ---------- close + submit ----------

  cancel(): void {
    this.visible.set(false);
    this.newTitle = '';
    this.newPrompt = '';
    this.newAgent = 'claude';
    this.newTaskType = 'chore';
    this.newTags = [];
    this.newTargetState = TaskState.Preparation;
    this.newMode = 'coding';
    this.newAllowWebAccess = false;
    this.newCliType = readDefaultCliPref();
    this.newModel = readDefaultModelPref(this.newCliType);
    this.newThinkingLevel = readDefaultThinkingLevelPref(this.newCliType);
    this.modelSelectionExplicit = false;
    this.availableModels.set([]);
    this.policySuggestion.set(null);
    this.suggestionRequest++;
    for (const att of this.newAttachments) URL.revokeObjectURL(att.previewUrl);
    this.newAttachments = [];
    this.routing.set(null);
    this.routingPending.set(false);
    this.routingRequest = null;
  }

  /**
   * Persist the draft. On success: store the chosen watch path as
   * "last used", upload pending attachments (if any), close the dialog,
   * fire `submitted$` so the shell can refresh.
   */
  submit(): void {
    const route = this.routing();
    if (this.routingPending() || route?.requiresQuestion) {
      this.errorDialog.show(new Error(route?.questionReason || 'Ownership routing is still being resolved.'), {
        title: 'Resolve task ownership',
        fallbackMessage: 'Choose the primary owner before creating this task.',
        source: 'Task routing',
      });
      return;
    }
    const attachments = this.newAttachments;
    const promptDraft = this.newPrompt.trim();
    const watchPath = this.newWatchPath;

    // When attachments are present we defer writing the prompt to the create
    // call (its `pending-attachment-…` placeholders are not yet resolvable),
    // upload each image against the new jobId, then PUT prompt.md with the
    // real `attachments/<file>` references.
    const initialPrompt = attachments.length > 0 ? undefined : (promptDraft || undefined);

    this.jobService.createJob({
      title: this.newTitle.trim(),
      watchPath,
      agent: this.newCliType,
      promptMarkdown: initialPrompt,
      targetState: this.newTargetState,
      cliType: this.newCliType,
      model: this.newModel.trim() || undefined,
      thinkingLevel: this.newThinkingLevel || undefined,
      modelExplicit: this.modelSelectionExplicit,
      thinkingLevelExplicit: this.modelSelectionExplicit,
      taskType: this.newTaskType,
      tags: this.newTags.length > 0 ? [...this.newTags] : undefined,
      kind: 'task',
      mode: this.newMode,
      allowWebAccess: this.newAllowWebAccess,
      routing: this.routingRequest ?? undefined,
      requestedTaskPrefix: route?.allowedTicketPrefix ?? undefined,
    }).subscribe({
      next: (res) => {
        localStorage.setItem('lastCreateWatchPath', watchPath);
        if (attachments.length > 0) {
          void this.uploadCreateAttachments(res.id, watchPath, promptDraft, attachments);
        }
        this.cancel();
        this.submittedSubject.next({ jobId: res.id });
      },
      error: (err) => {
        this.jobService.error.set(err.error || 'Failed to create job');
        this.errorDialog.show(err, {
          title: 'Failed to create task',
          fallbackMessage: 'Failed to create task',
          source: 'Task creation',
        });
      },
    });
  }

  // ---------- internal ----------

  private loadCreateModels(cliType: CliType): void {
    // ADR-0046: prefer the shared cache so the Create dialog renders the
    // model dropdown synchronously when the user opens it.
    if (this.catalogStore.hasFresh(cliType)) {
      const models = [...this.catalogStore.modelsFor(cliType)];
      this.availableModels.set(models);
      if (!this.newModel) {
        const def = models.find((m) => m.isDefault);
        if (def) {
          this.newModel = def.id;
          this.newThinkingLevel = this.newThinkingLevel ?? def.defaultThinkingLevel ?? null;
        }
      }
      return;
    }
    this.catalogStore.ensure(cliType).subscribe({
      next: (models) => {
        const list = [...models];
        this.availableModels.set(list);
        if (!this.newModel) {
          const def = list.find((m) => m.isDefault);
          if (def) {
            this.newModel = def.id;
            this.newThinkingLevel = this.newThinkingLevel ?? def.defaultThinkingLevel ?? null;
          }
        }
      },
      error: () => this.availableModels.set([]),
    });
  }

  private refreshPolicySuggestion(): void {
    const request = ++this.suggestionRequest;
    this.routingPolicy.getModelRoutingRecommendation(this.newTaskType, this.newCliType).subscribe({
      next: (suggestion) => {
        if (request !== this.suggestionRequest) return;
        this.policySuggestion.set(suggestion);
        this.applyPolicySuggestion();
      },
      error: () => {
        if (request === this.suggestionRequest) this.policySuggestion.set(null);
      },
    });
  }

  private applyPolicySuggestion(): void {
    if (this.modelSelectionExplicit) return;
    const suggestion = this.policySuggestion();
    if (!suggestion) return;
    this.newModel = suggestion.model;
    this.newThinkingLevel = suggestion.thinkingLevel;
  }

  private async uploadCreateAttachments(
    jobId: string,
    watchPath: string,
    promptDraft: string,
    attachments: PendingAttachment[],
  ): Promise<void> {
    let prompt = promptDraft;
    for (const att of attachments) {
      try {
        const form = new FormData();
        form.append('file', att.file, att.file.name || `${att.alt}.png`);
        const url = `/api/tasks/${encodeURIComponent(jobId)}/attachments`
          + (watchPath ? `?watchPath=${encodeURIComponent(watchPath)}` : '');
        // The job folder was created milliseconds ago. Backend caches can
        // race against that creation under concurrent polling, returning a
        // transient 400/404 "Job not found" before the cache observes the
        // mutation. Retry once with a short backoff so a brief cache miss
        // never surfaces as a user-visible upload failure.
        let res = await sessionFetch(url, { method: 'POST', body: form });
        if (!res.ok && (res.status === 400 || res.status === 404)) {
          await new Promise(r => setTimeout(r, 250));
          // FormData stream is consumed - rebuild it for the retry.
          const retry = new FormData();
          retry.append('file', att.file, att.file.name || `${att.alt}.png`);
          res = await sessionFetch(url, { method: 'POST', body: retry });
        }
        if (!res.ok) {
          this.errorDialog.show(new Error(`Upload failed (${res.status}) for ${att.file.name || att.alt}`), {
            title: 'Attachment upload failed',
            fallbackMessage: 'Could not upload one of the pasted images.',
            source: `Task ${jobId}`,
          });
          continue;
        }
        const payload = (await res.json()) as { fileName: string; relativePath: string };
        prompt = prompt.replace(
          new RegExp(`pending-attachment-${escapeRegex(att.id)}`, 'g'),
          payload.relativePath,
        );
      } catch (e) {
        this.errorDialog.show(e as Error, {
          title: 'Attachment upload failed',
          fallbackMessage: 'Could not upload one of the pasted images.',
          source: `Task ${jobId}`,
        });
      } finally {
        URL.revokeObjectURL(att.previewUrl);
      }
    }
    this.jobService.updateJobFile(jobId, 'prompt.md', prompt, watchPath).subscribe({
      next: () => this.submittedSubject.next({ jobId }),
      error: (err) => this.errorDialog.show(err, {
        title: 'Failed to save prompt',
        fallbackMessage: 'Attachments uploaded, but writing prompt.md failed.',
        source: `Task ${jobId}`,
      }),
    });
  }
}

// ---------- module-private helpers ----------

function readDefaultCliPref(): CliType {
  const stored = localStorage.getItem('defaultCliType') as CliType | null;
  if (stored && (CLI_TYPES as string[]).includes(stored)) return stored;
  return 'claude';
}

function readDefaultModelPref(cliType: CliType): string {
  return localStorage.getItem('defaultModel:' + cliType) ?? '';
}

function readDefaultThinkingLevelPref(cliType: CliType): string | null {
  return localStorage.getItem('defaultThinkingLevel:' + cliType) ?? null;
}

function deriveDraftTitle(text: string): string {
  if (!text) return '';
  for (const raw of text.split('\n')) {
    const line = raw.replace(/^#+\s*/, '').replace(/[*_`]/g, '').trim();
    if (line.length === 0) continue;
    return line.length > 80 ? line.slice(0, 77).trim() + '...' : line;
  }
  return '';
}

function escapeRegex(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function pickCreateWatchPath(
  paths: readonly WatchPathEntry[],
  active: ReadonlySet<string>,
): string {
  if (paths.length === 0) return '';
  const last = localStorage.getItem('lastCreateWatchPath');
  const isValid = (p: string | null) => !!p && paths.some((wp) => wp.path === p);
  const activePaths = paths.filter((wp) => active.has(wp.name));

  if (activePaths.length === 1) {
    return activePaths[0].path;
  }
  if (activePaths.length > 1) {
    const lastInActive = activePaths.find((wp) => wp.path === last);
    if (lastInActive) return lastInActive.path;
    return activePaths[0].path;
  }
  if (isValid(last)) return last as string;
  return paths[0].path;
}
