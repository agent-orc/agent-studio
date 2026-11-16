import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector,
  computed, effect, inject, input, output, signal, untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, catchError, finalize, map, of, switchMap, tap, throwError } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { CliModelSelectorComponent } from '../../../../components/cli-model-selector';
import { CliCatalogStore } from '../../../cli';
import { ActionPanelCoordinator, closeWhenAnotherPanelOpens } from '../../../../services/action-panel-coordinator.service';
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
import { createDossierCard, resolveDossierCardId } from '../../state/dossier-card-creation';
import {
  actionErrorMessage,
  answeredDecisionCount,
  bounded,
  cardPrompt,
  createOperationId,
  featureTaskDraft,
  answerSaveState,
  hasDecisionInput,
  laneLabel,
  readDefaultCli,
  readDefaultModel,
  readDefaultThinkingLevel,
  reworkPrompt,
  selectedDecisionText,
  unlinkedCardFailure,
} from '../workbench-decision-panel/workbench-decision-panel.util';
import { bindFormEscape, focusAfterRender } from '../workbench-decision-panel/workbench-decision-focus.util';

type ActionMode = 'feature-spawn' | 'rework' | null;
type DecisionOutcome = Exclude<ActionMode, null> | 'record-only';
/** The coordinator id all decision forms of one Dossier share; they show the same draft. */
const DECISION_PANEL = 'dossier-decision';

@Component({
  selector: 'app-workbench-decision-actions',
  standalone: true,
  imports: [CliModelSelectorComponent, PendingButtonDirective, StatusIndicatorComponent],
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
  private readonly catalog = inject(CliCatalogStore);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly panels = inject(ActionPanelCoordinator, { optional: true });
  private restoredDraftKey = '';
  /** The bar control that opened the confirmation form; closing it returns focus there. */
  private opener = 'workbench-decision-start';
  private readonly operationId = signal('');

  readonly mode = signal<ActionMode>(null);
  readonly title = signal('');
  readonly goal = signal('');
  readonly request = signal('');
  readonly validationError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly createdCard = signal<WorkbenchDecisionDraftCard | null>(null);
  readonly saving = signal(false);
  readonly cliType = signal<CliType>(readDefaultCli());
  readonly model = signal(readDefaultModel(readDefaultCli()));
  readonly thinkingLevel = signal<string | null>(readDefaultThinkingLevel(readDefaultCli()));

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly answeredCount = computed(() => answeredDecisionCount(this.decisionPoints(), this.responses()));
  readonly answersComplete = computed(() => this.answeredCount() === this.decisionPoints().length);
  readonly noteCount = computed(() => this.responses().filter(response => response.comment?.trim()).length);
  /** Whether the Dossier holds the answers on screen; any answer set can be saved (AGT-3875). */
  readonly saveState = computed(() => this.settled() ? 'none' : answerSaveState(this.document(), this.responses(),
    this.drafts.draft(this.projectName(), this.document().workbench.id)?.responses));
  readonly canSaveAnswers = computed(() => !this.settled() && !this.pending()
    && hasDecisionInput(this.responses()) && this.saveState() !== 'saved');
  readonly selectedSummary = computed(() => selectedDecisionText(
    this.decisionPoints(), this.responses()));
  readonly hasReworkComment = computed(() =>
    this.responses().some(response => Boolean(response.comment?.trim())));
  readonly hasReworkInput = computed(() =>
    Boolean(this.request().trim()) || this.hasReworkComment());
  /** The source concept card that runs the revision (AGT-3703). */
  readonly revisionCard = computed(() => this.document().revisionCard ?? null);
  readonly revisionCardLane = computed(() => laneLabel(this.revisionCard()?.lane ?? null));
  readonly reworkAvailable = computed(() => this.revisionCard()?.available === true);
  readonly reworkDisabledReason = computed(() => this.reworkAvailable()
    ? null
    : this.revisionCard()?.reason
      ?? 'No source concept card is known for this Dossier, so no card can run the revision.');
  readonly canPrepareFeature = computed(() =>
    !this.settled() && !this.pending() && this.answersComplete());
  readonly canCreateFeature = computed(() =>
    !this.settled() && this.answersComplete() && this.mode() === 'feature-spawn'
    && this.title().trim().length > 0 && this.goal().trim().length > 0);
  readonly canRequestRework = computed(() =>
    !this.settled() && !this.pending() && this.reworkAvailable());
  readonly canConfirmRework = computed(() =>
    this.canRequestRework() && this.mode() === 'rework' && this.hasReworkInput());
  readonly promptPreview = computed(() => cardPrompt(
    this.document(), this.featureDraft(), this.decisionPoints(), this.responses()));

  constructor() {
    effect(() => {
      const project = this.projectName();
      const id = this.document().workbench.id;
      const key = `${project}\u0000${id}`;
      const draft = this.drafts.draft(project, id);
      if (draft?.mode) untracked(() => this.panels?.claimIfFree(DECISION_PANEL));
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
        this.request.set(draft.request ?? '');
        this.restoreAgent(draft);
      } else {
        this.mode.set(null);
        this.operationId.set('');
        if (key !== this.restoredDraftKey) {
          this.title.set('');
          this.goal.set('');
          this.request.set('');
          this.createdCard.set(null);
        }
      }
      if (key !== this.restoredDraftKey) untracked(() => this.preselectDefaultModel());
      this.restoredDraftKey = key;
    });
    // Another action panel of this view opened: close the form so it cannot cover it (AGT-3875).
    closeWhenAnotherPanelOpens(this.panels, DECISION_PANEL, () => {
      if (this.mode() === null || this.pending()) return;
      this.drafts.closeAction(this.projectName(), this.document().workbench.id);
      this.mode.set(null);
      this.operationId.set('');
    });
    // Escape closes the open form even below another modal-stack entry (AGT-3829).
    bindFormEscape(computed(() => this.mode() !== null),
      () => this.host.nativeElement.querySelector('.decision-actions__confirmation'),
      () => { if (this.pending()) return false; this.closeConfirmation(); return true; });
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

  updateRequest(event: Event): void {
    const request = (event.target as HTMLTextAreaElement).value;
    this.request.set(request);
    this.drafts.updateAction(this.projectName(), this.document().workbench.id, { request });
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
    this.panels?.claim(DECISION_PANEL);
    this.mode.set('feature-spawn');
    this.opener = 'workbench-decision-start';
    this.focusAfterRender('workbench-decision-title');
  }

  /** Writes the answers and notes as `recordedResponses` through the decision gate. */
  saveAnswers(): void {
    if (!this.canSaveAnswers()) return;
    const actor = this.actor().trim();
    if (!actor) { this.invalid('Add the decision owner.'); return; }
    this.resetFeedback();
    const request = { ...this.baseRequest('record-only', actor, null), operationId: createOperationId() };
    this.saving.set(true);
    this.store.recordChoices(this.projectName(), this.document().workbench.id, request)
      .pipe(catchError(error => this.fail(error)), finalize(() => this.saving.set(false)))
      .subscribe(() => this.finish());
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
    this.request.set(draft.request ?? '');
    this.panels?.claim(DECISION_PANEL);
    this.mode.set('rework');
    this.opener = 'workbench-decision-rework';
    this.focusAfterRender('workbench-decision-rework-request');
  }

  closeConfirmation(event?: Event): void {
    event?.preventDefault();
    event?.stopPropagation();
    if (this.pending()) return;
    this.drafts.closeAction(this.projectName(), this.document().workbench.id);
    this.mode.set(null);
    this.operationId.set('');
    this.panels?.release(DECISION_PANEL);
    this.focusAfterRender(this.opener);
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
    if (!this.canConfirmRework() || this.pending()) return;
    const request = this.prepareRequest('rework');
    if (!request) return;
    this.store.requestRework(
      this.projectName(), this.document().workbench.id, request,
      reworkPrompt(this.document(), this.decisionPoints(), this.responses(), this.request()),
      this.agent(),
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
    return createDossierCard(this.tasks, this.projectName(), {
      title: draft.title,
      agent: this.cliType(),
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
    }).pipe(
      tap(card => {
        this.createdCard.set(card);
        this.drafts.rememberCreatedCard(this.projectName(), this.document().workbench.id, card);
        this.tasks.refresh();
      }),
    );
  }

  /** Resolve an older draft's missing id; removal must never use its display key. */
  private resolveCardId(card: WorkbenchDecisionDraftCard): Observable<WorkbenchDecisionDraftCard> {
    return resolveDossierCardId(this.tasks, this.projectName(), card).pipe(tap(resolved => {
      if (!resolved.id || resolved === card) return;
      this.createdCard.set(resolved);
      this.drafts.rememberCreatedCard(this.projectName(), this.document().workbench.id, resolved);
    }));
  }

  /**
   * The server removes the card of a decision it could not record and says so
   * in its answer. Without that answer the card is kept for the retry, which
   * links it under the same operation id.
   */
  private settleUnlinkedCard(card: WorkbenchDecisionDraftCard, error: unknown): Observable<never> {
    const failure = unlinkedCardFailure(card, error);
    if (failure.removed) {
      this.createdCard.set(null);
      this.drafts.clearCreatedCard(this.projectName(), this.document().workbench.id);
    }
    return throwError(() => failure.error);
  }

  private prepareRequest(outcome: Exclude<ActionMode, null>): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    if (outcome === 'rework') {
      if (!this.hasReworkInput()) return this.invalid('Describe what the revision should change.');
      return { ...this.baseRequest(outcome, actor, null), request: this.request().trim() || null };
    }
    if (!this.answersComplete()) return this.invalid('Answer every inline decision point first.');
    const task = this.featureDraft();
    if (!task.title || !task.goal) return this.invalid('Title and goal are required.');
    return this.baseRequest(outcome, actor, task);
  }

  private baseRequest(
    outcome: DecisionOutcome, actor: string, task: WorkbenchTaskDraft | null,
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
    // The prompt owns the decision list; repeating it in the goal duplicated it (AGT-3701).
    const draft = this.drafts.beginFeature(this.projectName(), this.document().workbench.id, {
      actor: this.actor(),
      title: `Implement ${this.document().workbench.title}`,
      goal: bounded(this.document().workbench.summary.trim(), 20_000),
      operationId,
    }, this.responses());
    this.title.set(draft.title);
    this.goal.set(draft.goal);
    this.operationId.set(draft.operationId ?? operationId);
    this.createdCard.set(draft.createdCard);
    this.drafts.updateAction(this.projectName(), this.document().workbench.id, this.agent());
  }

  private featureDraft(): WorkbenchTaskDraft {
    return featureTaskDraft(this.document(), this.projectName(), this.title(),
      this.goal(), this.selectedSummary());
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

  /** Preselect the picker default once per Dossier, preserving later explicit choices. */
  private preselectDefaultModel(): void {
    if (this.model().trim()) return;
    const cli = this.cliType();
    const project = this.projectName();
    const id = this.document().workbench.id;
    this.catalog.ensure(cli).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: models => {
        if (this.model().trim() || this.cliType() !== cli
          || this.projectName() !== project || this.document().workbench.id !== id) return;
        const preferred = models.find(model => model.available !== false && model.isDefault);
        if (!preferred) return;
        this.model.set(preferred.id);
        if (!this.thinkingLevel()) this.thinkingLevel.set(preferred.defaultThinkingLevel ?? null);
        if (this.mode()) this.drafts.updateAction(project, id, this.agent());
      },
      error: () => undefined,
    });
  }

  /** Move focus to the counterpart of a control the form swap removed (AGT-3829). */
  private focusAfterRender(testId: string): void {
    focusAfterRender(this.host.nativeElement, this.injector, testId, 'button, input, textarea');
  }

  private finish(): void {
    this.panels?.release(DECISION_PANEL);
    this.drafts.discard(this.projectName(), this.document().workbench.id);
    this.decisionChanged.emit();
  }

  private fail(error: unknown): Observable<never> {
    this.actionError.set(actionErrorMessage(error));
    requestAnimationFrame(() => this.host.nativeElement
      .querySelector<HTMLElement>('.decision-actions__error')
      ?.scrollIntoView({ block: 'nearest' }));
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
