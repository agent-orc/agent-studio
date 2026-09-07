import { ChangeDetectionStrategy, Component, HostListener, computed, inject, input, output, signal } from '@angular/core';
import type { OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { OrchestratorContextSourceOption } from '../../models/orchestrator-context-source.model';
import {
  OrchestratorContextSourceService,
  type OrchestratorContextSourceSearchResult,
} from '../../services/orchestrator-context-source.service';

const EMPTY_RESULTS: OrchestratorContextSourceSearchResult = {
  tasks: [], wiki: [], files: [], commits: [], degraded: false,
};

@Component({
  selector: 'app-orchestrator-context-picker',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-context-picker.component.html',
  styleUrl: './orchestrator-context-picker.component.scss',
})
export class OrchestratorContextPickerComponent implements OnDestroy {
  private readonly sources = inject(OrchestratorContextSourceService);
  readonly project = input.required<string>();
  readonly automaticLabel = input.required<string>();
  readonly currentSource = input<OrchestratorContextSourceOption | null>(null);
  readonly selectedIds = input<ReadonlySet<string>>(new Set<string>());
  readonly disabled = input(false);
  readonly attachmentAdded = output<OrchestratorContextSourceOption>();

  readonly open = signal(false);
  readonly query = signal('');
  readonly loading = signal(false);
  readonly results = signal<OrchestratorContextSourceSearchResult>(EMPTY_RESULTS);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private requestVersion = 0;

  readonly groups = computed(() => [
    { id: 'tasks', label: 'Tasks', items: this.results().tasks },
    { id: 'wiki', label: 'Wiki and Dossiers', items: this.results().wiki },
    { id: 'files', label: 'Files', items: this.results().files },
    { id: 'commits', label: 'Commits', items: this.results().commits },
  ] as const);
  readonly hasResults = computed(() => this.groups().some(group => group.items.length > 0));

  show(): void {
    if (!this.disabled()) this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }

  add(source: OrchestratorContextSourceOption): void {
    if (this.selectedIds().has(source.id)) return;
    this.attachmentAdded.emit(source);
  }


  onQuery(value: string): void {
    this.query.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    const query = value.trim();
    if (query.length < 2) {
      this.loading.set(false);
      this.results.set(EMPTY_RESULTS);
      return;
    }
    this.loading.set(true);
    const version = ++this.requestVersion;
    this.searchTimer = setTimeout(() => this.sources.search(this.project(), query).subscribe({
      next: result => {
        if (version !== this.requestVersion) return;
        this.results.set(result);
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.results.set({ ...EMPTY_RESULTS, degraded: true });
        this.loading.set(false);
      },
    }), 180);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.close();
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.requestVersion += 1;
  }
}
