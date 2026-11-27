import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { catchError, map, of, switchMap } from 'rxjs';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  WorkbenchReviewVerdict,
} from '../../../../models/project-docs.model';
import { formatRelativeTime, formatTime } from '../../../../services/format.util';
import { NowTickService } from '../../../../services/now-tick.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';
import { focusLost } from '../workbench-decision-panel/workbench-decision-focus.util';
import { WorkbenchReviewTagComponent } from '../workbench-review-tag/workbench-review-tag.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';

import {
  conceptCardKeys,
  ghostStatus,
  implementationStatusFor,
  normalizeKey,
  uniqueKeys,
  withDossierCards,
} from './workbench-viewer-header.util';

const MAX_INLINE_TASKS = 8;

@Component({
  selector: 'app-workbench-viewer-header',
  standalone: true,
  imports: [
    AppTooltipDirective,
    CopyableTaskKeyComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchDecisionPanelComponent,
    WorkbenchReviewTagComponent,
    StatusIndicatorComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer-header.component.html',
  styleUrl: './workbench-viewer-header.component.scss',
})
export class WorkbenchViewerHeaderComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input<readonly WorkbenchDecisionPoint[]>([]);
  readonly responses = input<readonly WorkbenchDecisionResponse[]>([]);
  readonly liveConnected = input(false);
  readonly connectionChangedAtUtc = input<string | null>(null);
  readonly lastUpdatedAtUtc = input<string | null>(null);
  readonly decisionChanged = output<void>();
  readonly draftDiscarded = output<void>();
  readonly manualRefresh = output<void>();
  readonly reviewChanged = output<void>();
  readonly readOnly = input(false);

  private readonly docs = inject(ProjectDocsService);
  private readonly tasks = inject(TaskService);
  private readonly now = inject(NowTickService).now;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly referenceKeys = signal<string[]>([]);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);
  readonly taskStatusesLoading = signal(false);
  readonly reviewOpen = signal(false);
  readonly reviewSaving = signal(false);
  readonly reviewError = signal<string | null>(null);
  readonly reviewVerdict = signal<WorkbenchReviewVerdict>('current');
  readonly reviewSupersededBy = signal('');
  readonly reviewNote = signal('');

  readonly inlineTaskStatuses = computed(() => this.taskStatuses().slice(0, MAX_INLINE_TASKS));
  readonly hiddenTaskCount = computed(() =>
    Math.max(0, this.taskStatuses().length - MAX_INLINE_TASKS),
  );
  readonly openDecisionCount = computed(() => {
    const workbench = this.document().workbench;
    if (
      workbench.decision?.state === 'succeeded' ||
      workbench.status === 'decided' ||
      workbench.status === 'archived'
    )
      return 0;
    if (workbench.status !== 'decision-pending' && workbench.phase !== 'decision-ready'
      && workbench.lifecycleState !== 'review-requested') return 0;
    // Browser selections remain drafts until saved.
    return workbench.openDecisionCount ?? Math.max(1, this.decisionPoints().length);
  });
  readonly implementationStatus = computed(() => {
    const document = this.document();
    return implementationStatusFor(
      document.workbench.status,
      withDossierCards(this.taskStatuses(), document, this.projectName()),
      conceptCardKeys(document),
    );
  });
  readonly statusLabel = computed(() => {
    const workbench = this.document().workbench;
    const label = this.implementationStatus() ?? humanize(workbench.status);
    const phase = workbench.status === 'decided' || workbench.status === 'documented'
      || workbench.status === 'archived' ? null : workbench.phase;
    return phase ? `${label} · ${humanize(phase)}` : label;
  });
  readonly connectionLabel = computed(() => {
    const state = this.liveConnected() ? 'Connected' : 'Disconnected';
    const changedAt = this.connectionChangedAtUtc();
    if (!changedAt) return state;
    return `${state} since ${formatTime(changedAt)} · ${formatRelativeTime(changedAt, this.now())}`;
  });
  readonly lastUpdateLabel = computed(() => {
    const updatedAt = this.lastUpdatedAtUtc();
    if (!updatedAt) return 'Not yet loaded';
    return `${formatTime(updatedAt)} · ${formatRelativeTime(updatedAt, this.now())}`;
  });
  readonly staleAsOfLabel = computed(() => {
    const updatedAt = this.lastUpdatedAtUtc();
    return updatedAt ? `Updates paused · as of ${formatTime(updatedAt)}` : 'Updates paused';
  });
  readonly liveStatusTooltip = computed(() =>
    `${this.connectionLabel()}\nLast update ${this.lastUpdateLabel()}`,
  );

  constructor() {
    // Opened from a control that closed (Needs you): start at the heading, not the body (AGT-3829).
    afterNextRender(() => {
      if (focusLost()) this.host.nativeElement.querySelector<HTMLElement>('h1')?.focus({ preventScroll: true });
    });
    effect((onCleanup) => {
      const workbench = this.document().workbench;
      const projectName = this.projectName();
      const fallbackKeys = uniqueKeys([
        ...workbench.sourceTaskKeys,
        ...(workbench.relatedTaskKeys ?? []),
      ]);
      const workbenchKey = normalizeKey(workbench.key);
      this.referenceKeys.set([]);
      this.taskStatuses.set([]);
      this.taskStatusesLoading.set(true);

      const keys$ = workbenchKey
        ? this.docs.getWorkbenchReferences(projectName, workbenchKey).pipe(
            map((references) =>
              uniqueKeys([
                ...references.items.flatMap((item) => (item.sourceKey ? [item.sourceKey] : [])),
                ...references.legacyTaskKeys,
                ...fallbackKeys,
              ]),
            ),
            catchError(() => of(fallbackKeys)),
          )
        : of(fallbackKeys);

      const subscription = keys$
        .pipe(
          switchMap((keys) => {
            this.referenceKeys.set(keys);
            if (keys.length === 0) return of([] as TaskReferenceStatus[]);
            return this.tasks.getReferenceStatuses(keys).pipe(
              map((statuses) => {
                const byKey = new Map(statuses.map((status) => [normalizeKey(status.key), status]));
                return keys.map(
                  (key) => byKey.get(normalizeKey(key)) ?? ghostStatus(key, projectName),
                );
              }),
              catchError(() => of(keys.map((key) => ghostStatus(key, projectName)))),
            );
          }),
        )
        .subscribe({
          next: (statuses) => {
            this.taskStatuses.set(statuses);
            this.taskStatusesLoading.set(false);
          },
        });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  closeDetails(disclosure: HTMLDetailsElement): void {
    disclosure.open = false;
  }

  openReview(): void {
    const review = this.document().workbench.review;
    this.reviewVerdict.set(review?.verdict ?? 'current');
    this.reviewSupersededBy.set(review?.supersededBy.join(', ') ?? '');
    this.reviewNote.set(review?.note ?? '');
    this.reviewError.set(null);
    this.reviewOpen.set(true);
  }

  setReviewVerdict(event: Event): void {
    this.reviewVerdict.set((event.target as HTMLSelectElement).value as WorkbenchReviewVerdict);
  }
  setReviewSupersededBy(event: Event): void {
    this.reviewSupersededBy.set((event.target as HTMLInputElement).value);
  }
  setReviewNote(event: Event): void {
    this.reviewNote.set((event.target as HTMLInputElement).value);
  }
  saveReview(): void {
    if (this.readOnly() || this.reviewSaving()) return;
    const supersededBy = [...new Set(this.reviewSupersededBy().split(/[\s,]+/).map(key => key.trim().toUpperCase()).filter(Boolean))];
    this.reviewSaving.set(true);
    this.reviewError.set(null);
    this.docs.recordWorkbenchReview(this.projectName(), this.document().workbench.id, {
      verdict: this.reviewVerdict(),
      supersededBy,
      reviewedBy: 'Operator',
      note: this.reviewNote().trim(),
    }).subscribe({
      next: () => {
        this.reviewSaving.set(false);
        this.reviewOpen.set(false);
        this.reviewChanged.emit();
      },
      error: error => {
        this.reviewSaving.set(false);
        this.reviewError.set(error?.error?.error || 'The review could not be recorded.');
      },
    });
  }
}

function humanize(value: string): string {
  const words = value.replaceAll('-', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}
