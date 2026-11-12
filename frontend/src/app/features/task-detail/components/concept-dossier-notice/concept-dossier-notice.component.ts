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
import { FormsModule } from '@angular/forms';
import { TaskInfo, TaskState, type ConceptDossierSummary } from '../../../../models/task.model';
import { laneName } from '../../../../models/lane-presentation';
import { NotificationService } from '../../../../services/notification.service';
import { studioProjectSlug } from '../../../../services/studio-project-slug.util';
import { TaskService } from '../../../../services/task.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { discoverWorkbenchDecisionMarkup } from '../../../../services/workbench-decision-markup.util';
import { catchError, of, switchMap } from 'rxjs';

type EditorKind = 'path' | 'no-dossier' | null;

@Component({
  selector: 'app-concept-dossier-notice',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './concept-dossier-notice.component.html',
  styleUrl: './concept-dossier-notice.component.scss',
})
export class ConceptDossierNoticeComponent {
  private readonly tasks = inject(TaskService);
  private readonly notifications = inject(NotificationService);
  private readonly docs = inject(ProjectDocsService);

  readonly job = input.required<TaskInfo>();
  readonly changed = output<void>();

  private readonly summaryOverride = signal<ConceptDossierSummary | null>(null);
  readonly decisionReceipt = signal<{ recorded: number; total: number; spawned: string[] } | null>(null);
  readonly dossierId = signal<string | null>(null);
  readonly summary = computed(() => this.summaryOverride() ?? this.job().conceptDossier ?? null);
  readonly dossierPath = computed(() => this.job().dossier?.path?.trim()
    || this.summary()?.repoRelativePath?.trim() || null);
  readonly wikiPath = computed(() => this.dossierPath()?.replace(/^docs\//i, '') ?? null);
  readonly wikiHref = computed(() => {
    const path = this.wikiPath();
    if (!path) return null;
    const base = `#/projects/${studioProjectSlug(this.job().projectName)}`;
    return this.dossierId() ? `${base}/workbenches/${encodeURIComponent(this.dossierId()!)}`
      : `${base}/wiki?page=${encodeURIComponent(path)}`;
  });
  readonly published = computed(() => this.job().mode !== 'concept'
    || this.job().integration?.status === 'integrated'
    || !!this.dossierId());
  readonly publicationStage = computed(() => {
    switch (this.job().integration?.status) {
      case 'merged-locally': return 'Merged locally';
      case 'partial': return 'Partially integrated';
      case 'pending': return 'Awaiting integration';
      case 'conflict-skipped': return 'Integration conflict';
      case 'no-branch': return 'No delivery ref';
      default: return laneName(this.job().state);
    }
  });
  readonly eligibleLane = computed(() =>
    this.job().state === TaskState.HumanReview || this.job().state === TaskState.Completed);
  readonly readyToAccept = computed(() => this.job().state === TaskState.HumanReview);
  readonly show = computed(() =>
    !!this.dossierPath() || (this.job().mode === 'concept'
      && (!!this.summary()?.noDossierNeeded || this.eligibleLane())));
  readonly missing = computed(() =>
    this.eligibleLane() && !this.dossierPath() && !this.summary()?.noDossierNeeded);

  readonly editor = signal<EditorKind>(null);
  readonly pathDraft = signal('');
  readonly reasonDraft = signal('');
  readonly busy = signal(false);

  private readonly reset = effect(() => {
    void this.job().taskKey;
    this.summaryOverride.set(null);
    this.editor.set(null);
    this.pathDraft.set('');
    this.reasonDraft.set('');
  });

  private readonly loadDecision = effect(onCleanup => {
    const path = this.dossierPath();
    const project = this.job().projectName;
    this.decisionReceipt.set(null);
    this.dossierId.set(null);
    if (!path) return;
    const subscription = this.docs.getWorkbenchOverview(project).pipe(
      switchMap(overview => {
        const match = overview.items.find(item => item.projectName === project
          && item.workbench.entryPath.toLowerCase() === path.toLowerCase());
        if (!match) return of(null);
        this.dossierId.set(match.workbench.id);
        return this.docs.getWorkbench(project, match.workbench.id);
      }),
      catchError(() => of(null)),
    ).subscribe(document => {
      if (!document?.workbench.decision) return;
      const receipt = document.workbench.decision;
      const points = discoverWorkbenchDecisionMarkup(document.html).points;
      this.decisionReceipt.set({
        recorded: receipt.responses.filter(response => response.selectedOptionIds.length > 0).length,
        total: points.length || receipt.responses.length,
        spawned: receipt.spawnedTaskKeys,
      });
    });
    onCleanup(() => subscription.unsubscribe());
  });

  openPathEditor(): void {
    this.pathDraft.set(this.dossierPath() ?? 'docs/<slug>/index.html');
    this.editor.set('path');
  }

  openNoDossierEditor(): void {
    this.reasonDraft.set('');
    this.editor.set('no-dossier');
  }

  cancel(): void {
    this.editor.set(null);
  }

  savePath(): void {
    const path = this.pathDraft().trim();
    if (!path || this.busy()) return;
    this.persist({ path, noDossierNeeded: false });
  }

  saveNoDossier(): void {
    const reason = this.reasonDraft().trim();
    if (!reason || this.busy()) return;
    this.persist({ noDossierNeeded: true, reason });
  }

  private persist(body: { path?: string; noDossierNeeded: boolean; reason?: string }): void {
    const job = this.job();
    this.busy.set(true);
    this.tasks.setConceptDossier(job.id, body, job.watchPath).subscribe({
      next: (summary) => {
        this.summaryOverride.set(summary);
        this.busy.set(false);
        this.editor.set(null);
        this.changed.emit();
        this.notifications.success(
          summary.repoRelativePath ? 'Dossier path linked.' : 'No-dossier decision recorded.',
        );
      },
      error: (error) => {
        this.busy.set(false);
        const message = error?.error?.error || 'Could not update the dossier reference.';
        this.notifications.warning(message, 'Dossier update failed');
      },
    });
  }
}
