import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, forkJoin, of } from 'rxjs';
import type { TaskDetail } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { ErrorDialogService } from '../../../../services/error-dialog.service';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { needsPlanningAcceptWarning } from '../../state/triage-actions.model';
import { buildIsolatedHtmlSrcdoc } from '../../../../services/sandboxed-html.util';
import {
  buildDecisionSubmission,
  parseDecisionJson,
  parseEmbeddedDecision,
  type DecisionOption,
} from './decision-surface.model';
import type { DecisionSurfaceSubmission } from './decision-surface.model';

const HTML_PATH = 'results/decision.html' as const;
const JSON_PATH = 'results/decision.json' as const;

@Component({
  selector: 'app-decision-surface',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective, StatusIndicatorComponent],
  templateUrl: './decision-surface.component.html',
  styleUrl: './decision-surface.component.scss',
})
export class DecisionSurfaceComponent {
  readonly detail = input.required<TaskDetail>();
  readonly mutationsBlocked = input(false);
  readonly decisionApplied = output<void>();

  private readonly jobs = inject(TaskService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly frame = viewChild<ElementRef<HTMLIFrameElement>>('decisionFrame');
  readonly html = signal<string | null>(null);
  private readonly json = signal<string | null>(null);
  private loadVersion = 0;
  private loadedTaskStateKey: string | null = null;
  private selectionKey: string | null = null;

  readonly selectedOptionId = signal<string | null>(null);
  readonly freeSteer = signal('');
  readonly pendingOptionId = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  readonly hasArtifact = computed(() => this.html() !== null || this.json() !== null);
  readonly parsed = computed(() => {
    const json = this.json();
    if (json !== null) return parseDecisionJson(json);
    const html = this.html();
    return html === null
      ? { document: null, error: null }
      : parseEmbeddedDecision(html);
  });
  readonly document = computed(() => this.parsed().document);
  readonly contractError = computed(() => this.parsed().error);
  readonly isolatedHtml = computed(() => buildIsolatedHtmlSrcdoc(this.html() ?? ''));
  readonly artifactPath = computed<DecisionSurfaceSubmission['artifactPath']>(() =>
    this.json() !== null ? JSON_PATH : HTML_PATH,
  );
  readonly artifactLabel = computed(() => {
    if (this.html() !== null && this.json() !== null) return 'decision.html + decision.json';
    return this.json() !== null ? 'decision.json' : 'decision.html';
  });
  readonly selectedOption = computed<DecisionOption | null>(() => {
    const selectedId = this.selectedOptionId();
    return this.document()?.options.find((option) => option.id === selectedId) ?? null;
  });
  readonly canSubmit = computed(() => {
    if (this.mutationsBlocked() || this.pendingOptionId() !== null) return false;
    if (!this.selectedOption()) return false;
    return !this.document()?.steer.required || this.freeSteer().trim().length > 0;
  });
  readonly submitLabel = computed(() => {
    const action = this.selectedOption()?.action;
    if (!action) return 'Apply decision';
    if (action.kind === 'steer') return 'Apply and continue';
    switch (action.targetState) {
      case '2-ready':
        return 'Apply and requeue';
      case '5-human-review':
        return 'Apply and resolve';
      case '6-completed':
        return 'Apply and accept';
      case '7-archive':
        return 'Apply and abort';
    }
    return 'Apply decision';
  });

  constructor() {
    effect(() => {
      const info = this.detail().info;
      const taskStateKey = `${info.taskKey}:${info.state}`;
      if (taskStateKey === this.loadedTaskStateKey) return;
      this.loadedTaskStateKey = taskStateKey;
      const version = ++this.loadVersion;
      this.pendingOptionId.set(null);
      this.actionError.set(null);
      this.html.set(null);
      this.json.set(null);
      if (info.state !== TaskState.Escalated) return;

      forkJoin({
        html: this.jobs
          .readTaskFile(info.id, HTML_PATH, info.watchPath, 'workspace')
          .pipe(catchError(() => of(null))),
        json: this.jobs
          .readTaskFile(info.id, JSON_PATH, info.watchPath, 'workspace')
          .pipe(catchError(() => of(null))),
      })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe((result) => {
          if (version !== this.loadVersion) return;
          this.html.set(result.html);
          this.json.set(result.json);
        });
    });

    effect(() => {
      const document = this.document();
      const key = document ? `${this.detail().info.id}:${document.id}` : null;
      if (key === this.selectionKey) return;
      this.selectionKey = key;
      this.selectedOptionId.set(document?.recommendation?.optionId ?? null);
      this.freeSteer.set('');
    });

    effect(() => {
      const frame = this.frame();
      const srcdoc = this.isolatedHtml();
      if (frame) frame.nativeElement.srcdoc = srcdoc;
    });
  }

  selectOption(optionId: string): void {
    if (this.pendingOptionId() !== null) return;
    this.selectedOptionId.set(optionId);
  }

  updateSteer(event: Event): void {
    this.freeSteer.set((event.target as HTMLTextAreaElement).value);
  }

  submit(): void {
    const document = this.document();
    const option = this.selectedOption();
    if (!document || !option || !this.canSubmit()) return;
    const submission = buildDecisionSubmission(
      document,
      option,
      this.freeSteer(),
      this.artifactPath(),
    );
    if (
      submission.action.kind === 'move'
      && needsPlanningAcceptWarning(this.detail().info, submission.action.targetState)
    ) {
      void this.confirmPlanningMove(submission);
      return;
    }
    this.applySubmission(submission);
  }

  private async confirmPlanningMove(submission: DecisionSurfaceSubmission): Promise<void> {
    const info = this.detail().info;
    const confirmed = await this.confirmDialog.confirm({
      title: 'Planning task without follow-up cards',
      message:
        'This planning task has not spawned a follow-up card and has no ' +
        '"no follow-up intended" declaration. Accepting it risks completing a plan ' +
        'without creating its work. Accept anyway?',
      detail: info.title || info.id,
      confirmLabel: 'Accept anyway',
      cancelLabel: 'Keep in review',
      kind: 'danger',
    });
    if (confirmed) this.applySubmission(submission);
  }

  private applySubmission(submission: DecisionSurfaceSubmission): void {
    const info = this.detail().info;
    this.pendingOptionId.set(submission.optionId);
    this.actionError.set(null);

    const request = submission.action.kind === 'steer'
      ? this.jobs.continueJob(
          info.id,
          submission.prompt,
          info.watchPath,
          undefined,
          undefined,
          undefined,
          'steer',
        )
      : this.jobs.moveJob(
          info.id,
          submission.action.targetState,
          info.watchPath,
          undefined,
          submission.reason,
        );

    request.subscribe({
      next: () => this.decisionApplied.emit(),
      error: (error) => this.handleActionError(error, submission),
    });
  }

  private handleActionError(error: unknown, submission: DecisionSurfaceSubmission): void {
    this.pendingOptionId.set(null);
    const message = decisionActionError(error);
    this.actionError.set(message);
    this.errorDialog.show(error, {
      title: 'Failed to apply decision',
      fallbackMessage: message,
      source: `Task ${this.detail().info.id}, option ${submission.optionLabel}`,
    });
  }
}

function decisionActionError(error: unknown): string {
  if (typeof error === 'object' && error !== null) {
    const body = (error as { error?: unknown }).error;
    if (typeof body === 'string' && body.trim()) return body.trim();
    if (typeof body === 'object' && body !== null) {
      const detail = (body as { detail?: unknown; error?: unknown }).detail
        ?? (body as { error?: unknown }).error;
      if (typeof detail === 'string' && detail.trim()) return detail.trim();
    }
    const message = (error as { message?: unknown }).message;
    if (typeof message === 'string' && message.trim()) return message.trim();
  }
  return 'The task action failed. The selection and guidance were kept for retry.';
}
