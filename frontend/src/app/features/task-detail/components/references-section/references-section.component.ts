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
import {
  TaskInfo,
  TaskReferenceKind,
  TaskReferenceLink,
  TaskReferences,
  TASK_REFERENCE_KINDS,
  TaskState,
  taskDependencyKey,
  taskDependencyRequiresRelease,
} from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskSelectionService } from '../../state/task-selection.service';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WorkbenchListItem } from '../../../../models/project-docs.model';
import { ReleaseGateSectionComponent } from '../release-gate-section/release-gate-section.component';
import { stateLabel } from '../../../../services/format.util';

/**
 * F34 detail-view reference editor. Renders the typed cross-reference rows
 * (depends on / related to / blocked by / supersedes) as chip lists with an
 * inline add-input (autocomplete over every known stable key). Each chip is a
 * `KEY — short-title` link that routes to the target task.
 *
 * Mutations follow ADR-0046 (Optimistic-UI): a local working copy of the
 * references re-renders the chips instantly, the replace-all PUT fires in the
 * background, and a rejection (unknown key / self-ref / cycle — surfaced by the
 * backend's per-edge `errors[]`) reverts the chip and toasts the reason.
 */
@Component({
  selector: 'app-references-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, TooltipDirective, ReleaseGateSectionComponent],
  templateUrl: './references-section.component.html',
  styleUrl: './references-section.component.scss',
})
export class ReferencesSectionComponent {
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly notifications = inject(NotificationService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly docs = inject(ProjectDocsService);

  readonly info = input.required<TaskInfo>();

  /** Emitted after a successful write so the parent can re-fetch the detail. */
  readonly changed = output<void>();

  readonly kinds = TASK_REFERENCE_KINDS;
  readonly kindLabels: Record<TaskReferenceKind, string> = {
    dependsOn: 'Depends on',
    relatedTo: 'Related to',
    blockedBy: 'Blocked by',
    supersedes: 'Supersedes',
    followUpOf: 'Follow-up of',
    raisedFollowUps: 'Raised follow-up',
    workbenches: 'Sources',
  };

  /** Local working copy of the references; re-seeded whenever the job changes. */
  private readonly localRefs = signal<TaskReferences>(emptyRefs());
  /** Relation kind whose write is currently in flight (null when idle). */
  readonly busyKind = signal<TaskReferenceKind | null>(null);
  /** Edit mode: reveals the per-kind add-inputs and the per-chip remove
   *  buttons. Off by default so the section reads as a calm, navigable
   *  chip list; reset on job switch. */
  readonly adding = signal(false);
  /** Per-kind add-input drafts. */
  readonly drafts = signal<Record<TaskReferenceKind, string>>({
    dependsOn: '',
    relatedTo: '',
    blockedBy: '',
    supersedes: '',
    followUpOf: '',
    raisedFollowUps: '',
    workbenches: '',
  });

  /** Stable document keys available in the task's owning project. */
  private readonly workbenchIndex = signal(new Map<string, WorkbenchListItem>());

  /**
   * AGT-2029 reverse direction ("blocked-by-me"): the tasks that wait on THIS
   * one (their dependsOn names this task's key). Loaded on job switch from
   * `GET /tasks/{id}/dependents?kind=dependsOn`. Empty when nothing depends on
   * this task or it has no stable key.
   */
  readonly blocking = signal<TaskReferenceLink[]>([]);

  /** The section renders when there is either an outgoing ref or an incoming dependent. */
  readonly hasAnyReferences = computed(() =>
    this.totalCount() > 0 || this.blocking().length > 0 || (this.info().relatedWikiPages?.length ?? 0) > 0);

  readonly wikiPages = computed(() => this.info().relatedWikiPages ?? []);

  private lastSeededKey: string | null = null;
  private readonly seed = effect(() => {
    const info = this.info();
    // Re-seed on job switch (taskKey changes) so an open editor follows the
    // selected card; the parent's fileSaved → detail re-fetch also lands here.
    const refs = info.references ?? emptyRefs();
    if (this.lastSeededKey !== info.taskKey) {
      this.lastSeededKey = info.taskKey;
      this.adding.set(false);
      this.loadBlocking(info);
      this.loadWorkbenchIndex(info);
    }
    if (this.busyKind() === null) {
      this.localRefs.set(cloneRefs(refs));
    }
  });

  /** Fetch the "blocked-by-me" direction for the current task (best-effort). */
  private loadBlocking(info: TaskInfo): void {
    this.blocking.set([]);
    if (!info.key) return; // a keyless task can never be depended on
    const forKey = info.taskKey;
    this.tasks.getTaskDependents(info.id, 'dependsOn', info.watchPath).subscribe({
      next: (links) => {
        // Ignore a stale response if the user has already switched cards.
        if (this.lastSeededKey === forKey) this.blocking.set(links ?? []);
      },
      error: () => {
        if (this.lastSeededKey === forKey) this.blocking.set([]);
      },
    });
  }

  private loadWorkbenchIndex(info: TaskInfo): void {
    this.workbenchIndex.set(new Map());
    if (!info.projectName) return;
    const forKey = info.taskKey;
    this.docs.getWorkbenches(info.projectName, true).subscribe({
      next: catalogue => {
        if (this.lastSeededKey !== forKey) return;
        const entries = catalogue.items
          .filter(item => item.valid && item.key)
          .map(item => [item.key!.toUpperCase(), item] as const);
        this.workbenchIndex.set(new Map(entries));
      },
      error: () => {
        if (this.lastSeededKey === forKey) this.workbenchIndex.set(new Map());
      },
    });
  }

  /** Short label for an incoming dependent chip: its key (or id) + title. */
  blockingLabel(link: TaskReferenceLink): string {
    const key = link.sourceKey ?? link.sourceJobId;
    return link.sourceTitle ? `${key} — ${truncate(link.sourceTitle, 40)}` : key;
  }

  /** Navigate to a task that depends on this one. */
  navigateToBlocking(link: TaskReferenceLink): void {
    const target = this.tasks
      .jobs()
      .find((t) => t.id === link.sourceJobId && t.watchPath === link.sourceWatchPath);
    if (!target) {
      this.notifications.info(
        `${link.sourceKey ?? link.sourceJobId} is not loaded in the current workspace view.`,
      );
      return;
    }
    this.selection.openDetail(target);
  }

  /** Self key (uppercased for compare); empty when the task has no F33 key. */
  private readonly selfKey = computed(() => (this.info().key ?? '').trim());

  /** Every known stable key → its task, for title resolution + autocomplete. */
  private readonly keyIndex = computed(() => {
    const map = new Map<string, TaskInfo>();
    for (const t of this.tasks.jobs()) {
      const k = (t.key ?? '').trim();
      if (k) map.set(k.toUpperCase(), t);
    }
    return map;
  });

  /** Total reference count across all kinds (drives the collapsed badge). */
  readonly totalCount = computed(() => {
    const r = this.localRefs();
    return r.dependsOn.length + r.relatedTo.length + r.blockedBy.length
      + r.supersedes.length + (r.followUpOf?.length ?? 0)
      + (r.raisedFollowUps?.length ?? 0) + (r.workbenches?.length ?? 0);
  });

  /** Autocomplete candidates for the selected key namespace. */
  candidateKeys(kind: TaskReferenceKind): string[] {
    if (kind === 'workbenches') {
      return [...this.workbenchIndex().values()]
        .map(item => item.key)
        .filter((key): key is string => Boolean(key))
        .sort((a, b) => a.localeCompare(b));
    }
    const self = this.selfKey().toUpperCase();
    return [...this.keyIndex().values()]
      .map((t) => (t.key ?? '').trim())
      .filter((k) => k && k.toUpperCase() !== self)
      .sort((a, b) => a.localeCompare(b));
  }

  /** Stable id for each relation's namespaced autocomplete list. */
  datalistId(kind: TaskReferenceKind): string {
    return `task-ref-keys-${this.info().id}-${kind}`;
  }

  refsFor(kind: TaskReferenceKind): string[] {
    const refs = this.localRefs()[kind];
    return kind === 'dependsOn'
      ? this.localRefs().dependsOn.map(taskDependencyKey)
      : [...((refs as string[] | undefined) ?? [])];
  }

  draftFor(kind: TaskReferenceKind): string {
    return this.drafts()[kind];
  }

  setDraft(kind: TaskReferenceKind, value: string): void {
    this.drafts.update((d) => ({ ...d, [kind]: value }));
  }

  /** Resolve a key to its short title, or null when the task isn't loaded. */
  titleFor(key: string): string | null {
    const normalized = key.trim().toUpperCase();
    return this.workbenchIndex().get(normalized)?.title
      ?? this.keyIndex().get(normalized)?.title
      ?? null;
  }

  chipLabel(kind: TaskReferenceKind, key: string): string {
    const target = this.keyIndex().get(key.trim().toUpperCase());
    if ((kind === 'followUpOf' || kind === 'raisedFollowUps') && target) {
      const state = isTerminalState(target.state) ? 'closed' : 'open';
      return `${key} · ${stateLabel(target.state)} · ${state}`;
    }
    const title = this.titleFor(key);
    return title ? `${key} — ${truncate(title, 48)}` : key;
  }

  chipTooltip(key: string): string {
    const title = this.titleFor(key);
    if (this.workbenchIndex().has(key.trim().toUpperCase())) {
      return title ? `${key}: ${title}` : key;
    }
    const releaseGate = this.releaseGateFor(key) ? ' · explicit release required' : '';
    return title ? `${key}: ${title}${releaseGate}` : `${key} (not loaded in this workspace view)${releaseGate}`;
  }

  /** A dependsOn target is satisfied once it reaches completed/archive. */
  isWaiting(kind: TaskReferenceKind, key: string): boolean {
    if (kind !== 'dependsOn') return false;
    const target = this.keyIndex().get(key.trim().toUpperCase());
    if (!target) return false;
    return !isTerminalState(target.state) || (this.releaseGateFor(key) && target.released !== true);
  }

  navigate(key: string): void {
    const normalized = key.trim().toUpperCase();
    const workbench = this.workbenchIndex().get(normalized);
    if (workbench) {
      this.tabs.open({
        kind: 'workbench',
        projectName: this.info().projectName,
        workbenchId: workbench.id,
        title: workbench.title,
      });
      return;
    }
    const target = this.keyIndex().get(normalized);
    if (!target) {
      this.notifications.info(`${key} is not loaded in the current workspace view.`);
      return;
    }
    this.selection.openDetail(target);
  }

  addFromDraft(kind: TaskReferenceKind): void {
    const raw = this.draftFor(kind).trim();
    if (!raw) return;
    this.add(kind, raw);
  }

  add(kind: TaskReferenceKind, keyRaw: string): void {
    const key = keyRaw.trim();
    if (!key) return;
    const upper = key.toUpperCase();
    if (upper === this.selfKey().toUpperCase()) {
      this.notifications.warning('A task cannot reference itself.');
      return;
    }
    const current = this.refsFor(kind);
    if (current.some((k) => k.toUpperCase() === upper)) {
      this.setDraft(kind, '');
      return;
    }
    const snapshot = this.localRefs();
    const nextValues = kind === 'dependsOn'
      ? [...snapshot.dependsOn, key]
      : [...this.refsFor(kind), key];
    const next = {
      ...cloneRefs(snapshot),
      [kind]: nextValues,
    } as TaskReferences;
    this.setDraft(kind, '');
    this.persist(kind, next, snapshot);
  }

  remove(kind: TaskReferenceKind, key: string): void {
    const snapshot = this.localRefs();
    const upper = key.toUpperCase();
    const next = {
      ...cloneRefs(snapshot),
      [kind]: kind === 'dependsOn'
        ? snapshot.dependsOn.filter((dependency) => taskDependencyKey(dependency).toUpperCase() !== upper)
        : ((snapshot[kind] as string[] | undefined) ?? [])
          .filter((value) => value.toUpperCase() !== upper),
    };
    this.persist(kind, next as TaskReferences, snapshot);
  }

  private releaseGateFor(key: string): boolean {
    const upper = key.trim().toUpperCase();
    const dependency = this.localRefs().dependsOn.find(
      (edge) => taskDependencyKey(edge).trim().toUpperCase() === upper,
    );
    return dependency ? taskDependencyRequiresRelease(dependency) : false;
  }

  private persist(kind: TaskReferenceKind, next: TaskReferences, snapshot: TaskReferences): void {
    const info = this.info();
    this.localRefs.set(next);
    this.busyKind.set(kind);
    this.tasks.setTaskReferences(info.id, next, info.watchPath).subscribe({
      next: (res) => {
        this.busyKind.set(null);
        // AGT-2029: an unknown key is saved (not rejected) - the referenced
        // task may be created later. Surface it as a non-blocking hint so the
        // operator knows the edge is open rather than silently mistyped.
        const warnings = res?.warnings ?? [];
        if (warnings.length > 0) {
          const keys = warnings.map((w) => w.target).join(', ');
          this.notifications.info(
            `Saved. ${keys} ${warnings.length === 1 ? 'does' : 'do'} not exist yet — the dependency stays open until it is created and completed.`,
          );
        }
        this.changed.emit();
      },
      error: (err) => {
        this.localRefs.set(snapshot);
        this.busyKind.set(null);
        this.notifications.error(extractReferenceError(err), 'Reference rejected');
      },
    });
  }

  toggleAdding(): void {
    this.adding.update((v) => !v);
  }

  openWikiPage(relPath: string): void {
    const projectName = this.info().projectName;
    if (!projectName) return;
    try {
      const key = `atp.projectWiki.v1.${projectName}`;
      const current = JSON.parse(localStorage.getItem(key) ?? '{}') as Record<string, unknown>;
      localStorage.setItem(key, JSON.stringify({ ...current, openedRel: relPath, viewerTab: 'doc' }));
    } catch { /* Navigation still opens the wiki when browser storage is unavailable. */ }
    this.tabs.open({
      kind: 'hub',
      projectName,
      section: 'wiki',
      wikiTarget: { kind: 'page', relPath },
    });
  }
}

function emptyRefs(): TaskReferences {
  return { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], followUpOf: [], raisedFollowUps: [], workbenches: [] };
}

function cloneRefs(r: TaskReferences): TaskReferences {
  return {
    dependsOn: [...r.dependsOn],
    relatedTo: [...r.relatedTo],
    blockedBy: [...r.blockedBy],
    supersedes: [...r.supersedes],
    followUpOf: [...(r.followUpOf ?? [])],
    raisedFollowUps: [...(r.raisedFollowUps ?? [])],
    workbenches: [...(r.workbenches ?? [])],
  };
}

function isTerminalState(state: string): boolean {
  return state === TaskState.Completed || state === TaskState.Archive;
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

/** Pull the first per-edge validation message out of a 400 body, else a fallback. */
function extractReferenceError(err: unknown): string {
  const body = (err as { error?: unknown })?.error;
  if (body && typeof body === 'object') {
    const errors = (body as { errors?: { message?: string }[] }).errors;
    if (Array.isArray(errors) && errors.length > 0 && errors[0]?.message) {
      return errors[0].message;
    }
    const topLevel = (body as { error?: string }).error;
    if (typeof topLevel === 'string') return topLevel;
  }
  return 'The reference could not be saved.';
}
