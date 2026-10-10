import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MenuComponent } from '../../../../components/menu/menu.component';
import type { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { TaskService } from '../../../../services/task.service';
import { CliCatalogStore } from '../../../cli';
import { StudioTabStateService } from '../../../studio-shell/services/studio-tab-state.service';
import {
  buildChatSessionGroups,
  type ChatScopeFilter,
  type ChatSessionRow,
} from '../../chat-session-list';
import { OrchestratorChatActivityService } from '../../state/orchestrator-chat-activity.service';
import { OrchestratorChatSessionsStore } from '../../state/orchestrator-chat-sessions.store';
import { buildWorkbenchContextKey } from '../orchestrator-side-sheet/orchestrator-context-key.util';

const NEW_CHAT_CURRENT = 'current';
const NEW_CHAT_GLOBAL = 'global';
const NEW_CHAT_PROJECT_PREFIX = 'project:';

/**
 * Left-sidebar session manager (AGT-3032): the one place that lists chat
 * sessions. Live sessions sit in Active, the rest in Recent by last activity,
 * archived ones in a collapsed group. Opening a row shows it in the
 * orchestrator side sheet; the host wires the three outputs to the sheet.
 */
@Component({
  selector: 'app-chat-session-manager',
  standalone: true,
  imports: [NgTemplateOutlet, MenuComponent, SectionHeaderComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-session-manager.component.html',
  styleUrl: './chat-session-manager.component.scss',
})
export class ChatSessionManagerComponent {
  readonly store = inject(OrchestratorChatSessionsStore);
  private readonly activity = inject(OrchestratorChatActivityService);
  private readonly tasks = inject(TaskService);
  private readonly catalog = inject(CliCatalogStore);
  private readonly studioTabs = inject(StudioTabStateService);

  /** Watched project names offered as "New chat" scopes. */
  readonly projects = input<readonly string[]>([]);

  readonly sessionOpened = output<string>();
  readonly locationRequested = output<string>();
  /** A context key to start in, or `null` for the side sheet's current scope. */
  readonly newChatRequested = output<string | null>();

  readonly query = signal('');
  readonly scope = signal<ChatScopeFilter>('all');
  readonly archivedOpen = signal(false);
  readonly newChatOpen = signal(false);
  readonly newChatAnchor = signal<HTMLElement | null>(null);
  /** Minute clock for the relative-time column. */
  private readonly now = signal(Date.now());

  readonly scopeOptions: readonly { value: ChatScopeFilter; label: string }[] = [
    { value: 'all', label: 'All scopes' },
    { value: 'projects', label: 'Projects' },
    { value: 'dossiers', label: 'Dossiers' },
    { value: 'tasks', label: 'Tasks' },
  ];

  private readonly workbenchTitles = computed<ReadonlyMap<string, string>>(() => {
    const titles = new Map<string, string>();
    for (const tab of this.studioTabs.tabs()) {
      if (tab.kind !== 'workbench' || !tab.key || !tab.title) continue;
      const key = buildWorkbenchContextKey(tab.projectName, tab.key);
      if (key) titles.set(key, tab.title);
    }
    return titles;
  });

  private readonly modelNames = computed<ReadonlyMap<string, string>>(() => {
    const names = new Map<string, string>();
    for (const entry of this.catalog.catalogs().values()) {
      for (const model of entry.models) {
        if (model.label && !names.has(model.id)) names.set(model.id, model.label);
      }
    }
    return names;
  });

  readonly groups = computed(() => {
    const jobs = this.tasks.jobs();
    const titles = this.workbenchTitles();
    const models = this.modelNames();
    return buildChatSessionGroups({
      sessions: this.store.sessions(),
      pendingContextKeys: this.activity.pendingContextKeys(),
      unreadContextKeys: this.store.unreadContextKeys(),
      query: this.query(),
      scope: this.scope(),
      now: this.now(),
      taskTitle: (projectId, taskKey) => jobs.find(job =>
        job.projectName === projectId
        && (job.taskKey === taskKey || job.displayKey === taskKey || job.key === taskKey))?.title ?? null,
      workbenchTitle: contextKey => titles.get(contextKey) ?? null,
      modelName: model => models.get(model) ?? null,
    });
  });

  readonly isEmpty = computed(() => {
    const groups = this.groups();
    return groups.active.length + groups.recent.length + groups.archived.length === 0;
  });
  readonly filtered = computed(() => this.query().trim() !== '' || this.scope() !== 'all');

  readonly newChatItems = computed<MenuItem[]>(() => {
    const items: MenuItem[] = [];
    const current = this.store.currentScope();
    if (current && current.kind !== 'project') {
      items.push({
        kind: 'row',
        id: NEW_CHAT_CURRENT,
        label: `${current.kind === 'task' ? 'Current task' : 'Current Dossier'}: ${current.label}`,
      });
    }
    items.push({ kind: 'row', id: NEW_CHAT_GLOBAL, label: 'Global' });
    const projects = this.projects();
    if (projects.length > 0) {
      items.push({ kind: 'header', label: 'Project' });
      for (const project of projects) {
        items.push({ kind: 'row', id: `${NEW_CHAT_PROJECT_PREFIX}${project}`, label: project });
      }
    }
    return items;
  });

  constructor() {
    const release = this.store.connect();
    const clock = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => {
      release();
      clearInterval(clock);
    });
  }

  onQuery(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  onScope(event: Event): void {
    this.scope.set((event.target as HTMLSelectElement).value as ChatScopeFilter);
  }

  toggleNewChat(anchor: HTMLElement): void {
    this.newChatAnchor.set(anchor);
    this.newChatOpen.update(open => !open);
  }

  onNewChat(event: MenuItemClickEvent): void {
    this.newChatOpen.set(false);
    if (event.id === NEW_CHAT_CURRENT) {
      this.newChatRequested.emit(null);
      return;
    }
    this.newChatRequested.emit(event.id);
  }

  open(row: ChatSessionRow): void {
    this.store.markSeen(row.contextKey);
    this.sessionOpened.emit(row.contextKey);
  }

  navigate(row: ChatSessionRow): void {
    this.locationRequested.emit(row.contextKey);
  }

  setArchived(row: ChatSessionRow, archived: boolean): void {
    this.store.setArchived(row.contextKey, archived);
  }

  rowLabel(row: ChatSessionRow): string {
    return [row.title, row.liveLabel, row.scopeLabel, row.modelLabel, row.tokenLabel, row.timeTitle]
      .filter(Boolean)
      .join(', ');
  }
}
