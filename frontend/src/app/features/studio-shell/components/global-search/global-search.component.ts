import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, effect, inject, input, model, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { TaskInfo } from '../../../../models/task.model';
import { BoardFiltersService } from '../../../board';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { GlobalSearchItem, GlobalSearchService } from './global-search.service';

interface RemoteResults {
  commits: GlobalSearchItem[];
  files: GlobalSearchItem[];
  dossiers: GlobalSearchItem[];
  errors: Record<string, string>;
}

const EMPTY_REMOTE: RemoteResults = { commits: [], files: [], dossiers: [], errors: {} };

@Component({
  selector: 'app-global-search',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './global-search.component.html',
  styleUrl: './global-search.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GlobalSearchComponent {
  private readonly api = inject(GlobalSearchService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly boardFilters = inject(BoardFiltersService);
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly open = model(false);
  readonly query = signal('');
  readonly remote = signal<RemoteResults>(EMPTY_REMOTE);
  readonly loading = signal(false);
  readonly activeIndex = signal(0);
  readonly inputRef = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private readonly focusWhenOpened = effect(() => {
    if (this.open()) queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  });
  private timer: ReturnType<typeof setTimeout> | null = null;
  private requestVersion = 0;

  readonly taskResults = computed<GlobalSearchItem[]>(() => {
    const q = this.query().trim().toLowerCase();
    if (q.length < 2) return [];
    return this.tasks()
      .filter(task => [task.key, task.title, task.state].some(value => value?.toLowerCase().includes(q)))
      .sort((a, b) => Number(b.key?.toLowerCase() === q) - Number(a.key?.toLowerCase() === q))
      .slice(0, 20)
      .map(task => ({
        domain: 'tasks', projectName: task.projectName, projectColor: this.projectColor(task.projectName),
        title: task.title, subtitle: task.key || task.id, taskKey: task.taskKey, lane: task.state,
      }));
  });

  readonly groups = computed(() => [
    { domain: 'tasks', label: 'Tasks', items: this.taskResults() },
    { domain: 'dossiers', label: 'Dossiers', items: this.remote().dossiers },
    { domain: 'commits', label: 'Commits', items: this.remote().commits },
    { domain: 'files', label: 'Files', items: this.remote().files },
  ] as const);
  readonly flatResults = computed(() => this.groups().flatMap(group => group.items));

  show(): void {
    this.open.set(true);
    queueMicrotask(() => this.inputRef()?.nativeElement.focus());
  }

  close(): void {
    this.open.set(false);
    this.query.set('');
    this.remote.set(EMPTY_REMOTE);
  }

  onQuery(value: string): void {
    this.query.set(value);
    this.activeIndex.set(0);
    if (this.timer) clearTimeout(this.timer);
    const q = value.trim();
    if (q.length < 2) {
      this.remote.set(EMPTY_REMOTE);
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    const version = ++this.requestVersion;
    this.timer = setTimeout(() => this.api.search(q).subscribe({
      next: result => {
        if (version !== this.requestVersion) return;
        this.remote.set({
          commits: result.commits, files: result.files,
          dossiers: result.dossiers ?? [], errors: result.errors,
        });
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.remote.set({ ...EMPTY_REMOTE, errors: { search: 'Git results are temporarily unavailable.' } });
        this.loading.set(false);
      },
    }), 120);
  }

  choose(item: GlobalSearchItem): void {
    if (item.domain === 'tasks' && item.taskKey) {
      const task = this.tasks().find(candidate => candidate.taskKey === item.taskKey);
      if (task) {
        this.tabs.open({ kind: 'task', taskKey: task.taskKey });
      }
    } else if (item.domain === 'dossiers' && item.dossierId) {
      // Same viewer tab the Dossier overview opens: project plus Dossier id,
      // never the repository path behind the document.
      this.tabs.open({
        kind: 'workbench',
        projectName: item.projectName,
        workbenchId: item.dossierId,
        title: item.title,
        ...(item.dossierKey ? { key: item.dossierKey } : {}),
      });
    } else if (item.domain === 'commits' && item.sha) {
      this.boardFilters.setSoleProject(item.projectName);
      this.tabs.open({ kind: 'diff', commitSha: item.sha });
    } else if (item.domain === 'files') {
      const wikiPath = item.isWiki ? item.path?.replace(/^docs\//i, '') : null;
      this.tabs.open({
        kind: 'hub',
        projectName: item.projectName,
        section: item.isWiki ? 'wiki' : 'git',
        ...(wikiPath ? { wikiTarget: { kind: 'page' as const, relPath: wikiPath } } : {}),
      });
    }
    this.close();
  }

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (this.open()) this.close();
      else this.show();
      return;
    }
    if (!this.open()) return;
    if (event.key === 'Escape') { event.preventDefault(); this.close(); return; }
    const results = this.flatResults();
    if (event.key === 'ArrowDown' && results.length) {
      event.preventDefault(); this.activeIndex.update(i => (i + 1) % results.length);
    } else if (event.key === 'ArrowUp' && results.length) {
      event.preventDefault(); this.activeIndex.update(i => (i - 1 + results.length) % results.length);
    } else if (event.key === 'Enter' && results[this.activeIndex()]) {
      event.preventDefault(); this.choose(results[this.activeIndex()]);
    }
  }

  resultIndex(item: GlobalSearchItem): number { return this.flatResults().indexOf(item); }

  /**
   * Stable row identity for `@for`. The subtitle alone is not unique across
   * Dossiers, which carry the shared `status · phase` chip there.
   */
  resultKey(item: GlobalSearchItem): string {
    const identity = item.dossierId ?? item.taskKey ?? item.sha ?? item.path ?? item.subtitle;
    return `${item.domain}:${item.projectName}:${identity}`;
  }

  private projectColor(name: string): string {
    let hash = 0;
    for (const char of name) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
    return `hsl(${Math.abs(hash) % 360} 58% 48%)`;
  }
}
