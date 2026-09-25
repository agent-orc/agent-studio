import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ProjectDocsService } from '../../../../../services/project-docs.service';
import { TaskService } from '../../../../../services/task.service';
import { TaskReferenceNavigationService } from '../../../../../services/task-reference-navigation.service';
import { WikiTreeNode, WorkbenchListItem } from '../../../../../models/project-docs.model';

interface Area { id: string; label: string; description: string; glossaryPath: string }
interface Term { term: string; definition: string; synonyms: string[] }
interface Glossary { areaId: string; label: string; path: string; exists: boolean; terms: Term[] }

@Component({
  selector: 'app-area-glossary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './area-glossary.component.html',
  styleUrl: './area-glossary.component.scss',
})
export class AreaGlossaryComponent {
  readonly projectName = input.required<string>();
  readonly roots = input<readonly WikiTreeNode[]>([]);
  readonly openPage = output<string>();
  readonly openDossier = output<WorkbenchListItem>();
  private readonly http = inject(HttpClient);
  private readonly docs = inject(ProjectDocsService);
  private readonly tasks = inject(TaskService);
  private readonly navigation = inject(TaskReferenceNavigationService);
  readonly areas = signal<Area[]>([]);
  readonly areaId = signal<string | null>(null);
  readonly glossary = signal<Glossary | null>(null);
  readonly dossiers = signal<WorkbenchListItem[]>([]);
  readonly area = computed(() => this.areas().find(value => value.id === this.areaId()) ?? null);
  readonly pages = computed(() => {
    const id = this.areaId();
    if (!id) return [];
    const pages: WikiTreeNode[] = [];
    const visit = (nodes: readonly WikiTreeNode[]): void => {
      for (const node of nodes) {
        if (node.type === 'folder') visit(node.children);
        else if (node.tags?.includes(id) && node.relPath !== this.glossary()?.path.replace(/^docs\//, '')) pages.push(node);
      }
    };
    visit(this.roots());
    return pages;
  });
  readonly cards = computed(() => {
    const id = this.areaId();
    if (!id) return [];
    const grouped = this.tasks.grouped();
    return Object.values(grouped).flatMap(value => Array.isArray(value) ? value : [])
      .filter(job => job.projectName === this.projectName() && job.tags?.includes(id));
  });
  readonly linkedDossiers = computed(() => this.dossiers().filter(item => item.tags?.includes(this.areaId() ?? '')));

  constructor() {
    effect(() => {
      const project = this.projectName();
      this.http.get<{ items: Area[] }>(`/api/projects/${encodeURIComponent(project)}/areas`).subscribe({
        next: response => this.areas.set(response.items), error: () => this.areas.set([]),
      });
      this.docs.getWorkbenches(project, true).subscribe({
        next: response => this.dossiers.set(response.items), error: () => this.dossiers.set([]),
      });
    });
  }

  select(id: string): void {
    this.areaId.set(id || null);
    this.glossary.set(null);
    if (!id) return;
    this.http.get<Glossary>(`/api/projects/${encodeURIComponent(this.projectName())}/areas/${encodeURIComponent(id)}/glossary`)
      .subscribe({ next: glossary => this.glossary.set(glossary), error: () => this.glossary.set(null) });
  }

  openCard(taskKey: string): void { this.navigation.openTaskKey(taskKey); }
}
