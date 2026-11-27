import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  output,
  viewChildren,
} from '@angular/core';
import { TreeRowComponent } from '@studio/ui/components/tree-row/tree-row.component';
import type { StudioIconName } from '@studio/ui/components/studio-icon/studio-icon.component';
import type { ArticlePattern, WorkbenchListItem } from '../../../../models/project-docs.model';

@Component({
  selector: 'app-explorer-workbench-history',
  standalone: true,
  imports: [TreeRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-workbench-history.component.html',
  styleUrl: './explorer-workbench-history.component.scss',
})
export class ExplorerWorkbenchHistoryComponent {
  readonly projectName = input.required<string>();
  readonly items = input.required<readonly WorkbenchListItem[]>();
  readonly activeWorkbenchId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly groups = computed(() => [
    {
      status: 'documented', label: 'Documented', empty: 'No documented items',
      items: this.items().filter(item => item.status === 'documented'),
    },
    {
      status: 'archived', label: 'Archived', empty: 'No archived items',
      items: this.items().filter(item => item.status === 'archived'),
    },
  ]);
  private readonly topics = viewChildren<ElementRef<HTMLElement>>('workbenchTopic');

  constructor() {
    effect(() => {
      const activeWorkbenchId = this.activeWorkbenchId();
      const activeTopic = this.topics()
        .map(topic => topic.nativeElement.querySelector<HTMLButtonElement>('[aria-current="page"]'))
        .find(topic => topic !== null);
      if (!activeWorkbenchId || !activeTopic || typeof activeTopic.scrollIntoView !== 'function') return;
      queueMicrotask(() => activeTopic.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
    });
  }

  isActive(item: WorkbenchListItem): boolean {
    return item.id === this.activeWorkbenchId();
  }

  secondaryMeta(item: WorkbenchListItem): string {
    return item.status === 'documented' ? 'Documented' : 'Archived';
  }

  documentPattern(item: WorkbenchListItem): ArticlePattern {
    return item.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(item: WorkbenchListItem): StudioIconName {
    return this.documentPattern(item) === 'ui' ? 'grid' : 'book';
  }

  tooltip(item: WorkbenchListItem): string {
    return [item.title, item.key, `${this.documentPattern(item)} pattern`, this.secondaryMeta(item)]
      .filter(Boolean)
      .join('\n');
  }
}
