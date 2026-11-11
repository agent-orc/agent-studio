import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Observable, catchError, map, of, switchMap, tap, throwError } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import {
  ConfirmWorkbenchDecisionRequest,
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDecisionResult,
  WorkbenchDocument,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';
import { CLI_TYPES, CliType } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import {
  WorkbenchDecisionDraftCard,
  WorkbenchDecisionDraftStore,
} from '../../state/workbench-decision-draft.store';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';
import {
  actionErrorMessage,
  bounded,
  cardPrompt,
  createOperationId,
  reworkPrompt,
  selectedDecisionText,
  taskKeyTail,
} from '../workbench-decision-panel/workbench-decision-panel.util';

type ActionMode = 'feature-spawn' | 'rework' | null;

@Component({
  selector: 'app-workbench-decision-actions',
  standalone: true,
  imports: [CliModelSelectorComponent, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-decision-actions.component.html',
  styleUrl: './workbench-decision-actions.component.scss',
})
export class WorkbenchDecisionActionsComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input<readonly WorkbenchDecisionPoint[]>([]);
  readonly responses = input<readonly WorkbenchDecisionResponse[]>([]);
  readonly actor = input.required<string>();
  readonly inline = input(false);
  readonly settled = input(false);
  readonly decisionChanged = output<void>();
  readonly archiveRequested = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);
  private readonly drafts = inject(WorkbenchDecisionDraftStore);
  private readonly tasks = inject(TaskService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private restoredDraftKey = '';
  private readonly operationId = signal('');

  readonly mode = signal<ActionMode>(null);
  readonly title = signal('');
  readonly goal = signal('');
  readonly validationError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly createdCard = signal<WorkbenchDecisionDraftCard | null>(null);
  readonly cliType = signal<CliType>(readDefaultCli());
  readonly model = signal(readDefaultModel(readDefaultCli()));
  readonly thinkingLevel = signal<string | null>(readDefaultThinkingLevel(readDefaultCli()));

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly answersComplete = computed(() => {
    if (this.decisionPoints().length === 0) return true;
    const responses = new Map(this.responses().map(response => [response.decisionId, response]));
    return this.decisionPoints().every(point =>
      (responses.get(point.id)?.selectedOptionIds.length ?? 0) > 0);
  });
  readonly selectedSummary = computed(() => selectedDecisionText(
    this.decisionPoints(), this.responses()));
  readonly hasReworkComment = computed(() =>
    this.responses().some(response => Boolean(response.comment?.trim())));
  readonly reworkAvailable = computed(() => Boolean(this.document().workbench.key));
  readonly reworkDisabledReason = computed(() => this.reworkAvailable()
    ? null
    : 'Request rework requires the Dossier orchestrator session delivered by AGT-2725.');
  readonly canPrepareFeature = computed(() =>
    !this.settled() && !this.pending() && this.answersComplete());
  readonly canCreateFeature = computed(() =>
    this.canPrepareFeature() && this.mode() === 'feature-spawn'
    && this.title().trim().length > 0 && this.goal().trim().length > 0);
  readonly canRequestRework = computed(() =>
    !this.settled() && !this.pending() && this.reworkAvailable() && this.hasReworkComment());
  readonly promptPreview = computed(() => cardPrompt(
    this.document(), this.featureDraft(), this.decisionPoints(), this.responses()));

  constructor() {
    effect(() => {
      const project = this.projectName();
      const id = this.document().workbench.id;
      const key = `${project}\u0000${id}`;
      const draft = this.drafts.draft(project, id);
      if (draft?.mode === 'feature-spawn') {
        this.mode.set('feature-spawn');
        this.title.set(draft.title);
        this.goal.set(draft.goal);
        this.operationId.set(draft.operationId ?? '');
        this.createdCard.set(draft.createdCard);
        this.restoreAgent(draft);
      } else if (draft?.mode === 'rework') {
        this.mode.set('rework');
        this.operationId.set(draft.operationId ?? '');
        this.restoreAgent(draft);
      } else {
        this.mode.set(null);
        this.operationId.set('');
        if (key !== this.restoredDraftKey) {
          this.title.set('');
          this.goal.set('');
          this.createdCard.set(null);
        }
      }
      this.restoredDraftKey = key;
    });
  }

  updateTitle(event: Event): void {
    const title = (event.target as HTMLInputElement).value;
    this.title.set(title);
    this.drafts.updateFeature(this.projectName(), this.document().workbench.id, { title });
  }

  updateGoal(event: Event): void {
    const goal = (event.target as HTMLTextAreaElement).value;
    this.goal.set(goal);
    this.drafts.updateFeature(this.projectName(), this.document().workbench.id, { goal });
  }

  updateAgent(change: { cliType: CliType; model: string; thinkingLevel: string | null }): void {
    this.cliType.set(change.cliType);
    this.model.set(change.model);
    this.thinkingLevel.set(change.thinkingLevel);
    this.drafts.updateAction(this.projectName(), this.document().workbench.id, change);
  }

  prepareFeatureCard(): void {
    if (!this.canPrepareFeature()) return;
    this.seedFeatureDraft();
    this.mode.set('feature-spawn');
  }

  beginRework(): void {
    if (!this.canRequestRework()) return;
    this.resetFeedback();
    const operationId = createOperationId();
    const draft = this.drafts.beginRework(this.projectName(), this.document().workbench.id, {
      actor: this.actor(), operationId, cliType: this.cliType(), model: this.model(),
      thinkingLevel: this.thinkingLevel(),
    }, this.responses());
    this.operationId.set(draft.operationId ?? operationId);
    this.mode.set('rework');
  }

  closeConfirmation(event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    if (this.pending()) return;
    this.drafts.closeAction(this.projectName(), this.document().workbench.id);
    this.mode.set(null);
    this.operationId.set('');
    queueMicrotask(() => this.focusPrimaryAction());
  }

  createFeatureCard(): void {
    if (!this.canCreateFeature() || this.pending()) return;
    const request = this.prepareRequest('feature-spawn');
    if (!request?.task) return;
    this.actionError.set(null);
    this.store.prepare(this.projectName(), this.document().workbench.id, request).pipe(
      switchMap(prepared => this.createCard(prepared.taskDraft ?? request.task!).pipe(
        switchMap(card => this.resolveCardId(card)),
        map(card => ({ prepared, card })))),
      switchMap(({ prepared, card }) => this.confirmFeature(request, card, prepared).pipe(
        catchError(error => this.settleUnlinkedCard(card, error)),
      )),
      catchError(error => this.fail(error)),
    ).subscribe(() => this.finish());
  }

  confirmRework(): void {
    if (this.mode() !== 'rework' || !this.canRequestRework() || this.pending()) return;
    const request = this.prepareRequest('rework');
    const key = this.document().workbench.key;
    const agent = this.agent();
    if (!request || !key) return;
    if (!agent.model) {
      this.invalid('Choose a model before requesting rework.');
      return;
    }
    this.store.requestRework(
      this.projectName(), this.document().workbench.id, key, request,
      reworkPrompt(this.document(), this.decisionPoints(), this.responses()), agent,
    ).pipe(catchError(error => this.fail(error))).subscribe(() => this.finish());
  }

  private confirmFeature(
    prepared: PrepareWorkbenchDecisionRequest,
    card: WorkbenchDecisionDraftCard,
    result: WorkbenchDecisionResult,
  ): Observable<WorkbenchDecisionResult> {
    const request: ConfirmWorkbenchDecisionRequest = {
      ...prepared,
      expectedRevision: result.revision,
      expectedFingerprint: result.fingerprint,
      spawnedTaskKeys: [card.key],
      spawnedTaskIds: card.id ? [card.id] : [],
      ...this.agent(),
      confirmed: true,
    };
    return this.store.confirm(this.projectName(), this.document().workbench.id, request);
  }

  private createCard(draft: WorkbenchTaskDraft): Observable<WorkbenchDecisionDraftCard> {
    if (this.createdCard()) return of(this.createdCard()!);
    return this.tasks.getWatchPaths().pipe(
      map(entries => {
        const path = entries.find(entry => entry.name === this.projectName())?.path;
        if (!path) throw new Error(`Could not resolve the task path for ${this.projectName()}.`);
        return path;
      }),
      switchMap(watchPath => this.tasks.createJob({
        title: draft.title,
        agent: this.cliType(),
        watchPath,
        promptMarkdown: cardPrompt(this.document(), draft, this.decisionPoints(), this.responses()),
        targetState: draft.initialLane,
        cliType: this.cliType(),
        model: this.model().trim() || undefined,
        thinkingLevel: this.thinkingLevel() || undefined,
        modelExplicit: true,
        thinkingLevelExplicit: true,
        taskType: draft.taskType,
        mode: draft.mode,
        dossier: { path: this.document().workbench.entryPath },
      }).pipe(switchMap(created => this.tasks.getDetailByProject(created.id, this.projectName()).pipe(
        map(detail => ({ detail, id: created.id, watchPath })),
        catchError(() => this.tasks.deleteJob(created.id, watchPath).pipe(
          catchError(() => throwError(() => new Error(
            `The task was created, but its details could not be read. The task may remain on the board. Ask a Studio operator to remove task ${created.id}.`))),
          switchMap(() => throwError(() => new Error('The task was created, but its details could not be read. The task was removed. Try again.'))),
        )),
      )))),
      map(({ detail, id, watchPath }) => ({
        id,
        watchPath,
        key: detail.info.key || detail.info.displayKey || taskKeyTail(detail.info.taskKey),
        taskKey: detail.info.taskKey,
        title: detail.info.title,
        lane: detail.info.state,
      })),
      tap(card => {
        this.createdCard.set(card);
        this.drafts.rememberCreatedCard(this.projectName(), this.document().workbench.id, card);
        this.tasks.refresh();
      }),
    );
  }

  /**
   * A card restored from an older draft may lack its job id. The server
   * removes an unlinked card by id only, so the id is looked up first; a card
   * that cannot be found keeps no id and is never removed by its display key.
   */
  private resolveCardId(card: WorkbenchDecisionDraftCard): Observable<WorkbenchDecisionDraftCard> {
    if (card.id) return of(card);
    return this.tasks.getDetailByProject(card.taskKey || card.key, this.projectName()).pipe(
      map(detail => detail.info.id ? { ...card, id: detail.info.id, watchPath: detail.info.watchPath } : card),
      tap(resolved => {
        if (!resolved.id) return;
        this.createdCard.set(resolved);
        this.drafts.rememberCreatedCard(this.projectName(), this.document().workbench.id, resolved);
      }),
      catchError(() => of(card)),
    );
  }

  /**
   * The server removes the card of a decision it could not record and says so
   * in its answer. Without that answer the card is kept for the retry, which
   * links it under the same operation id.
   */
  private settleUnlinkedCard(card: WorkbenchDecisionDraftCard, error: unknown): Observable<never> {
    const payload = (error as HttpErrorResponse | null)?.error as Partial<WorkbenchDecisionResult> | null;
    if (card.id && payload?.removedTaskIds?.includes(card.id)) {
      this.createdCard.set(null);
      this.drafts.clearCreatedCard(this.projectName(), this.document().workbench.id);
      return throwError(() => error);
    }
    if (card.id && payload?.retainedTaskIds?.includes(card.id)) return throwError(() => error);
    if (!card.id)
      return throwError(() => new Error(
        `The Dossier decision could not be saved. Task ${card.key} could not be checked, so it was not removed and may remain on the board without a Dossier link. Remove it before you try again.`));
    if (payload?.error) return throwError(() => error);
    return throwError(() => new Error(
      `The Dossier decision could not be confirmed. Task ${card.key} was created and is kept for the retry. Try again to link it.`));
  }

  private prepareRequest(outcome: Exclude<ActionMode, null>): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    if (outcome === 'rework') {
      if (!this.hasReworkComment()) return this.invalid('Add a comment that explains what should change.');
      return this.baseRequest(outcome, actor, null);
    }
    if (!this.answersComplete()) return this.invalid('Answer every inline decision point first.');
    const task = this.featureDraft();
    if (!task.title || !task.goal) return this.invalid('Title and goal are required.');
    return this.baseRequest(outcome, actor, task);
  }

  private baseRequest(
    outcome: Exclude<ActionMode, null>, actor: string, task: WorkbenchTaskDraft | null,
  ): PrepareWorkbenchDecisionRequest {
    this.validationError.set(null);
    return {
      operationId: this.operationId() || createOperationId(), outcome,
      expectedRevision: this.document().revision, expectedFingerprint: this.document().fingerprint,
      actor, archiveReason: null, task, responses: [...this.responses()],
    };
  }

  private seedFeatureDraft(): void {
    this.resetFeedback();
    const operationId = createOperationId();
    const decisions = this.selectedSummary();
    const draft = this.drafts.beginFeature(this.projectName(), this.document().workbench.id, {
      actor: this.actor(),
      title: `Implement ${this.document().workbench.title}`,
      goal: bounded([this.document().workbench.summary.trim(), decisions
        ? `Recorded decisions:\n${decisions}` : ''].filter(Boolean).join('\n\n'), 20_000),
      operationId,
    }, this.responses());
    this.title.set(draft.title);
    this.goal.set(draft.goal);
    this.operationId.set(draft.operationId ?? operationId);
    this.createdCard.set(draft.createdCard);
    this.drafts.updateAction(this.projectName(), this.document().workbench.id, this.agent());
  }

  private featureDraft(): WorkbenchTaskDraft {
    return {
      title: this.title().trim() || `Implement ${this.document().workbench.title}`,
      goal: this.goal().trim(),
      acceptanceCriteria: [
        'Implement every recorded Dossier selection and preserve its stated constraints.',
        'Verify the resulting behavior with the checks required by the affected surface.',
      ],
      evidenceLinks: [this.document().workbench.entryPath],
      chosenOption: bounded(this.selectedSummary(), 2_000) || null,
      relatedTaskKeys: this.document().workbench.sourceTaskKeys,
      targetProject: this.projectName(), initialLane: '2-ready', mode: 'coding', taskType: 'feature',
    };
  }

  private agent(): { cliType: CliType; model: string; thinkingLevel: string | null } {
    return { cliType: this.cliType(), model: this.model().trim(), thinkingLevel: this.thinkingLevel() };
  }

  private restoreAgent(draft: { cliType?: string; model?: string; thinkingLevel?: string | null }): void {
    if (draft.cliType && (CLI_TYPES as readonly string[]).includes(draft.cliType))
      this.cliType.set(draft.cliType as CliType);
    if (draft.model !== undefined) this.model.set(draft.model);
    if (draft.thinkingLevel !== undefined) this.thinkingLevel.set(draft.thinkingLevel);
  }

  private focusPrimaryAction(): void {
    this.host.nativeElement.querySelector<HTMLButtonElement>(
      '[data-testid="workbench-decision-start"]')?.focus();
  }

  private finish(): void {
    this.drafts.discard(this.projectName(), this.document().workbench.id);
    this.decisionChanged.emit();
  }

  private fail(error: unknown): Observable<never> {
    this.actionError.set(actionErrorMessage(error));
    return EMPTY;
  }

  private invalid(message: string): null {
    this.validationError.set(message);
    return null;
  }

  private resetFeedback(): void {
    this.validationError.set(null);
    this.actionError.set(null);
  }
}

function readDefaultCli(): CliType {
  const value = readStorage('defaultCliType');
  return value && (CLI_TYPES as readonly string[]).includes(value) ? value as CliType : 'claude';
}

function readDefaultModel(cli: CliType): string { return readStorage(`defaultModel:${cli}`) ?? ''; }
function readDefaultThinkingLevel(cli: CliType): string | null {
  return readStorage(`defaultThinkingLevel:${cli}`);
}
function readStorage(key: string): string | null {
  try { return globalThis.localStorage?.getItem(key) ?? null; }
  catch { return null; }
}
