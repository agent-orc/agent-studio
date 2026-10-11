import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TaskService } from '../../../../services/task.service';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { CliCatalogStore } from '../../../cli/catalog';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import {
  buildChatSessionGroups,
  type ChatSessionRow,
  type ChatSessionScopeFilter,
} from '../../state/chat-session-list';
import { OrchestratorChatActivityService } from '../../state/orchestrator-chat-activity.service';
import { OrchestratorChatSessionsStore } from '../../state/orchestrator-chat-sessions.store';
import {
  buildWorkbenchContextKey,
  parseOrchestratorContextKey,
} from '../orchestrator-side-sheet/orchestrator-context-key.util';

const SCOPE_OPTIONS: readonly { value: ChatSessionScopeFilter; label: string }[] = [
  { value: 'all', label: 'All scopes' },
  { value: 'projects', label: 'Projects' },
  { value: 'dossiers', label: 'Dossiers' },
  { value: 'tasks', label: 'Tasks' },
];

/**
 * The one place that lists orchestrator chat sessions (left Studio sidebar,
 * "Chats"). Live sessions sit in Active, everything else with at least one
 * persisted turn in one calm recency list, operator-archived and
 * task-archived sessions in a collapsed Archived group. Selecting a row opens
 * it in the orchestrator side sheet.
 */
@Component({
  selector: 'app-chat-sessions-panel',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    MenuComponent,
    SectionHeaderComponent,
    StudioIconComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-sessions-panel.component.html',
  styleUrl: './chat-sessions-panel.component.scss',
})
export class ChatSessionsPanelComponent implements OnInit, OnDestroy {
  /** Watched project names; each one is a "New chat" scope. */
  readonly projects = input<readonly string[]>([]);

  readonly store = inject(OrchestratorChatSessionsStore);
  private readonly activity = inject(OrchestratorChatActivityService);
  private readonly tasks = inject(TaskService);
  private readonly catalogs = inject(CliCatalogStore);
  private readonly studioTabs = inject(StudioTabStateService);

  readonly scopeOptions = SCOPE_OPTIONS;
  readonly query = signal('');
  readonly scope = signal<ChatSessionScopeFilter>('all');
  readonly archivedOpen = signal(false);
  readonly newChatOpen = signal(false);
  readonly newChatAnchor = signal<HTMLElement | null>(null);
  /** Re-renders relative times once a minute without refetching. */
  private readonly now = signal(Date.now());

  /** Catalogue display names across every loaded CLI catalogue, keyed by model id. */
  private readonly modelLabels = computed(() => {
    const labels = new Map<string, string>();
    for (const entry of this.catalogs.catalogs().values()) {
      for (const model of entry.models) {
        if (model.label && !labels.has(model.id)) labels.set(model.id, model.label);
      }
    }
    return labels;
  });

  /** Dossier titles of open Dossier tabs, keyed by `workbench:<PROJ>/<KEY>`. */
  private readonly workbenchTitles = computed(() => {
    const titles = new Map<string, string>();
    for (const tab of this.studioTabs.tabs()) {
      if (tab.kind !== 'workbench' || !tab.key || !tab.title) continue;
      const key = buildWorkbenchContextKey(tab.projectName, tab.key);
      if (key) titles.set(key, tab.title);
    }
    return titles;
  });

  readonly groups = computed(() => {
    const modelLabels = this.modelLabels();
    const workbenchTitles = this.workbenchTitles();
    const jobs = this.tasks.jobs();
    return buildChatSessionGroups({
      sessions: this.store.sessions(),
      pendingContextKeys: this.activity.pendingContextKeys(),
      unreadContextKeys: this.store.unreadContextKeys(),
      query: this.query(),
      scope: this.scope(),
      now: this.now(),
      modelLabel: model => modelLabels.get(model) ?? null,
      workbenchTitle: session => workbenchTitles.get(session.contextKey) ?? null,
      taskTitle: (session: OrchestratorContextSession) => jobs.find(job =>
        job.projectName === session.projectId
        && (job.taskKey === session.taskKey || job.displayKey === session.taskKey || job.key === session.taskKey))
        ?.title ?? null,
    });
  });

  readonly filtering = computed(() => this.query().trim().length > 0 || this.scope() !== 'all');

  readonly newChatItems = computed<MenuItem[]>(() => {
    const items: MenuItem[] = [];
    const current = parseOrchestratorContextKey(this.store.navigationContextKey());
    if (current?.kind === 'task' && current.taskKey) {
      items.push({ kind: 'row', id: current.key, label: `Current task ${current.taskKey}`, hint: current.projectId ?? undefined });
    } else if (current?.kind === 'workbench' && current.workbenchKey) {
      items.push({ kind: 'row', id: current.key, label: `Current Dossier ${current.workbenchKey}`, hint: current.projectId ?? undefined });
    }
    const projects = this.projects();
    if (projects.length > 0) {
      if (items.length > 0) items.push({ kind: 'separator' });
      items.push({ kind: 'header', label: 'Project' });
      for (const project of projects) items.push({ kind: 'row', id: `project:${project}`, label: project });
    }
    if (items.length === 0) {
      items.push({ kind: 'row', id: '__none__', label: 'No project available', disabled: true });
    }
    return items;
  });

  private detach: (() => void) | null = null;
  private clock: VisibleIntervalHandle | null = null;

  ngOnInit(): void {
    this.detach = this.store.attach();
    this.catalogs.hydrateAll();
    this.clock = setVisibleInterval(() => this.now.set(Date.now()), 60_000);
  }

  ngOnDestroy(): void {
    this.detach?.();
    this.detach = null;
    clearVisibleInterval(this.clock);
    this.clock = null;
  }

  isOpen(row: ChatSessionRow): boolean {
    return row.contextKey === this.store.openContextKey();
  }

  open(row: ChatSessionRow): void {
    this.store.requestOpen(row.contextKey);
  }

  navigate(row: ChatSessionRow, event: Event): void {
    event.stopPropagation();
    this.store.requestNavigate(row.contextKey);
  }

  archive(row: ChatSessionRow, event: Event): void {
    event.stopPropagation();
    if (!row.archivable) return;
    this.store.setArchived(row.contextKey, true);
  }

  unarchive(row: ChatSessionRow, event: Event): void {
    event.stopPropagation();
    this.store.setArchived(row.contextKey, false);
  }

  toggleNewChat(event: Event): void {
    this.newChatAnchor.set(event.currentTarget as HTMLElement);
    this.newChatOpen.update(open => !open);
  }

  onNewChat(event: MenuItemClickEvent): void {
    this.newChatOpen.set(false);
    if (event.id === '__none__') return;
    this.store.requestOpen(event.id);
  }

  onQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  onScope(event: Event): void {
    this.scope.set((event.target as HTMLSelectElement).value as ChatSessionScopeFilter);
  }

  rowTitle(row: ChatSessionRow): string {
    return [row.title, row.scopeLabel, row.modelLabel, row.tokensLabel].filter(Boolean).join(' · ');
  }
}
