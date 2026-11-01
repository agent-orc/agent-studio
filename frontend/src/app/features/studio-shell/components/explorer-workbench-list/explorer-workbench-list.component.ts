import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';

const STYLE_GUIDE_PATH = 'docs/operations/admin-design-guideline/index.html';

@Component({
  selector: 'app-explorer-workbench-list',
  standalone: true,
  imports: [TreeRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-workbench-list.component.html',
  styleUrl: './explorer-workbench-list.component.scss',
})
export class ExplorerWorkbenchListComponent {
  readonly projectName = input.required<string>();
  readonly activeWorkbenchId = input<string | null>(null);
  readonly overviewActive = input(false);
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly openOverview = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  readonly catalogue = signal<WorkbenchCatalogue | null>(null);
  readonly styleGuide = computed(() =>
    this.catalogue()?.items.find(item => normalizePath(item.entryPath) === STYLE_GUIDE_PATH) ?? null);
  readonly pendingDecisionCount = computed(() =>
    (this.catalogue()?.items ?? [])
      .filter(item => item.status === 'decision-pending')
      .reduce((count, item) => count + (item.openDecisionCount ?? 1), 0));
  readonly dossierActive = computed(() =>
    this.overviewActive() || (!!this.activeWorkbenchId() && this.activeWorkbenchId() !== this.styleGuide()?.id));

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      untracked(() => {
        this.catalogue.set(null);
        this.loadCatalogue(projectName);
      });
    });
    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event || event.projectName && event.projectName !== this.projectName()) return;
      untracked(() => this.loadCatalogue(this.projectName()));
    });
  }

  dossierAriaLabel(): string {
    const count = this.pendingDecisionCount();
    return count > 0 ? `Dossiers, ${count} decisions waiting` : 'Dossiers';
  }

  accessibleMeta(item: WorkbenchListItem): string {
    const days = Math.max(0, Math.floor((Date.now() - new Date(item.updatedAtUtc).getTime()) / 86_400_000));
    const updated = days === 0 ? 'updated today' : `updated ${days} days ago`;
    const pattern = item.pattern === 'ui' ? 'ui' : 'concept';
    const status = item.valid ? item.status === 'decision-pending' ? 'Decision pending' : item.status : item.error || 'Descriptor needs repair';
    return `${pattern} pattern, ${status}, ${updated}`;
  }

  navTooltip(item: WorkbenchListItem): string {
    return [item.title, item.key, this.accessibleMeta(item)].filter(Boolean).join('\n');
  }

  private loadCatalogue(projectName: string): void {
    this.docs.getWorkbenches(projectName, true).subscribe({
      next: value => {
        if (this.projectName() === projectName) this.catalogue.set(value);
      },
      error: () => undefined,
    });
  }
}

function normalizePath(value: string): string {
  return value.replace(/\\/g, '/').replace(/^\/+/, '');
}
