import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import {
  ConfirmWorkbenchDecisionRequest,
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionPoint,
  WorkbenchDecisionProjection,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
} from '../../../../models/project-docs.model';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionDraftStore } from '../../state/workbench-decision-draft.store';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';
import { WorkbenchDecisionActionsComponent } from '../workbench-decision-actions/workbench-decision-actions.component';
import {
  createOperationId,
  laneLabel,
  selectedDecisionText,
} from './workbench-decision-panel.util';

@Component({
  selector: 'app-workbench-decision-panel',
  standalone: true,
  imports: [
    DatePipe,
    PendingButtonDirective,
    TaskReferenceMicrocardComponent,
    WorkbenchDecisionActionsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-decision-panel.html',
  styleUrl: './workbench-decision-panel.scss',
})
export class WorkbenchDecisionPanelComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input<readonly WorkbenchDecisionPoint[]>([]);
  readonly responses = input<readonly WorkbenchDecisionResponse[]>([]);
  readonly inline = input(false);
  readonly decisionChanged = output<void>();
  readonly draftDiscarded = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);
  private readonly drafts = inject(WorkbenchDecisionDraftStore);
  private readonly tasks = inject(TaskService);
  private readonly publicDemo = inject(PublicDemoModeService);
  private readonly operationId = signal('');

  readonly actor = signal('Operator');
  readonly archiveOpen = signal(false);
  readonly archiveReason = signal('');
  readonly validationError = signal<string | null>(null);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly storedDecision = computed(() => this.document().workbench.decision ?? null);
  readonly reworkRevisionLanded = computed(() => {
    const decision = this.storedDecision();
    return decision?.outcome === 'rework'
      && Boolean(decision.sourceEntryFingerprint)
      && Boolean(this.document().entryFingerprint)
      && decision.sourceEntryFingerprint !== this.document().entryFingerprint;
  });
  readonly persistedDecision = computed(() =>
    this.reworkRevisionLanded() ? null : this.storedDecision());
  readonly result = computed(() => this.requestState().result);
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly settled = computed(() => {
    const decision = this.persistedDecision();
    if (decision) return decision.state === 'succeeded';
    return ['decided', 'documented', 'archived'].includes(this.document().workbench.status);
  });
  readonly gateReady = computed(() =>
    this.document().workbench.phase === 'decision-ready'
    || this.persistedDecision() !== null
    || this.settled());
  readonly mutationBlocked = computed(() =>
    this.publicDemo.readOnly()
    || this.document().workingTreeModified
    || (!this.document().revision && !this.document().fingerprint));
  readonly readOnly = this.publicDemo.readOnly;
  readonly stage = computed(() => this.reworkRevisionLanded()
    ? null
    : this.document().workbench.decisionStage ?? this.result()?.decisionStage ?? null);
  readonly selectedSummary = computed(() => selectedDecisionText(
    this.decisionPoints(), this.responses()));
  readonly browserDraft = computed(() =>
    this.drafts.draft(this.projectName(), this.document().workbench.id));
  readonly hasBrowserDraft = computed(() => this.browserDraft() !== null && !this.settled());

  constructor() {
    effect(() => {
      const decision = this.persistedDecision();
      const draft = this.browserDraft();
      if (decision?.confirmedBy || decision?.preparedBy)
        this.actor.set(decision.confirmedBy || decision.preparedBy);
      else if (draft?.actor) this.actor.set(draft.actor);
      if (this.settled()) this.drafts.discard(this.projectName(), this.document().workbench.id);
    });
    effect(onCleanup => {
      const keys = this.persistedDecision()?.spawnedTaskKeys ?? [];
      if (keys.length === 0) return this.taskStatuses.set([]);
      const subscription = this.tasks.getReferenceStatuses(keys).subscribe({
        next: statuses => this.taskStatuses.set(statuses),
        error: () => this.taskStatuses.set([]),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  updateActor(event: Event): void {
    const actor = (event.target as HTMLInputElement).value;
    this.actor.set(actor);
    if (this.hasBrowserDraft())
      this.drafts.updateAction(this.projectName(), this.document().workbench.id, { actor });
  }

  updateArchiveReason(event: Event): void {
    this.archiveReason.set((event.target as HTMLTextAreaElement).value);
  }

  beginArchive(): void {
    if (this.pending() || this.settled()) return;
    this.validationError.set(null);
    this.store.clear(this.projectName(), this.document().workbench.id);
    this.operationId.set(createOperationId());
    this.archiveOpen.set(true);
  }

  prepareArchive(): void {
    if (!this.archiveOpen() || this.pending()) return;
    const request = this.archiveRequest();
    if (!request) return;
    this.store.prepare(this.projectName(), this.document().workbench.id, request).subscribe({
      error: () => undefined,
    });
  }

  confirmArchive(): void {
    if (this.stage() !== 'prepared' || this.pending()) return;
    const prepared = this.archiveRequest();
    if (!prepared) return;
    const result = this.result();
    const request: ConfirmWorkbenchDecisionRequest = {
      ...prepared,
      expectedRevision: result?.revision ?? this.document().revision,
      expectedFingerprint: result?.fingerprint ?? this.document().fingerprint,
      spawnedTaskKeys: [],
      confirmed: true,
    };
    this.store.confirm(this.projectName(), this.document().workbench.id, request).subscribe({
      next: () => this.decisionChanged.emit(),
      error: () => undefined,
    });
  }

  cancelArchive(): void {
    if (this.pending()) return;
    this.archiveOpen.set(false);
    this.archiveReason.set('');
    this.operationId.set('');
    this.store.clear(this.projectName(), this.document().workbench.id);
    this.validationError.set(null);
  }

  discardDraft(): void {
    if (this.pending()) return;
    this.drafts.discard(this.projectName(), this.document().workbench.id);
    this.store.clear(this.projectName(), this.document().workbench.id);
    this.validationError.set(null);
    this.draftDiscarded.emit();
  }

  outcomeLabel(decision: WorkbenchDecisionProjection): string {
    if (decision.outcome === 'rework') return 'Rework requested';
    return decision.outcome === 'archive' ? 'Archived' : 'Created card';
  }

  stageLabel(): string {
    if (this.document().workbench.status === 'documented') return 'Documented';
    switch (this.stage()) {
      case 'prepared': return 'Ready to confirm';
      case 'pending': return 'Decision in progress';
      case 'failed': return 'Retry needed';
      case 'succeeded': return 'Decided';
      case 'archived': return 'Archived';
      default: return this.gateReady() ? 'Decision ready' : 'In progress';
    }
  }

  fallbackTaskTitle(): string {
    return this.persistedDecision()?.taskDraft?.title ?? 'Created feature card';
  }

  fallbackTaskLane(): string {
    return laneLabel(this.persistedDecision()?.taskDraft?.initialLane ?? null);
  }

  private archiveRequest(): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    const reason = this.archiveReason().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    if (!reason) return this.invalid('Add a reason before preparing the archive decision.');
    return {
      operationId: this.operationId() || createOperationId(),
      outcome: 'archive',
      expectedRevision: this.document().revision,
      expectedFingerprint: this.document().fingerprint,
      actor,
      archiveReason: reason,
      task: null,
      responses: [...this.responses()],
    };
  }

  private invalid(message: string): null {
    this.validationError.set(message);
    return null;
  }
}
