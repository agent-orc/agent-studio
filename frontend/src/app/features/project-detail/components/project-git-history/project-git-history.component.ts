import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, output } from '@angular/core';
import { formatCompactDateTime } from '../../../../services/format.util';
import {
  buildGitCommitChips,
  buildGitGraphRows,
  type GitGraphCommit,
} from '../../../git';

@Component({
  selector: 'app-project-git-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-history.component.html',
  styleUrl: './project-git-history.component.scss',
})
export class ProjectGitHistoryComponent {
  private readonly host = inject(ElementRef) as ElementRef<HTMLElement>;

  readonly commits = input.required<readonly GitGraphCommit[]>();
  readonly selectedSha = input<string | null>(null);
  readonly hasMore = input(false);
  readonly loadingMore = input(false);

  readonly commitSelected = output<GitGraphCommit>();
  readonly changesRequested = output<GitGraphCommit>();
  readonly loadMore = output<void>();

  readonly rows = computed(() => buildGitGraphRows(this.commits()).map(row => ({
    ...row,
    chips: buildGitCommitChips(row.commit),
  })));

  constructor() {
    effect(() => {
      const sha = this.selectedSha();
      if (!sha) return;
      this.rows();
      queueMicrotask(() => {
        const row = [...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-sha]')]
          .find(candidate => candidate.dataset['sha'] === sha);
        if (typeof row?.scrollIntoView === 'function') row.scrollIntoView({ block: 'nearest' });
      });
    });
  }

  when(value: string): string {
    return formatCompactDateTime(value);
  }

  selectTaskCommit(event: MouseEvent, commit: GitGraphCommit): void {
    event.stopPropagation();
    this.commitSelected.emit(commit);
  }

  selectFromSummary(event: Event, commit: GitGraphCommit): void {
    event.preventDefault();
    event.stopPropagation();
    this.commitSelected.emit(commit);
  }

  selectFromRowKey(event: Event, commit: GitGraphCommit): void {
    if (event.target !== event.currentTarget) return;
    this.selectFromSummary(event, commit);
  }

  containScrollKeys(event: KeyboardEvent): void {
    const list = event.currentTarget as HTMLElement;
    const moves: Record<string, number> = {
      ArrowUp: -48, ArrowDown: 48, PageUp: -list.clientHeight, PageDown: list.clientHeight,
      Home: -list.scrollHeight, End: list.scrollHeight,
    };
    if (!(event.key in moves) || (event.target as HTMLElement).matches('button, input, textarea, select')) return;
    event.preventDefault();
    event.stopPropagation();
    list.scrollBy({ top: moves[event.key] });
  }
}
