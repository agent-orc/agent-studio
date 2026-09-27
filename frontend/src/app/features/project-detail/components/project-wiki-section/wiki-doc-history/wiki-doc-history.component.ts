import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { WikiFileHistory } from '../../../../../models/project-docs.model';

/**
 * Read-only provenance + git-history panel for a single wiki document. Shows
 * which model last touched the doc, when, and why (frontmatter `model:` /
 * `last-distilled:` / `why:` win, else the latest commit's Co-authored-by
 * trailer supplies the model), followed by the file's commit log newest-first.
 *
 * Purely presentational: the parent owns loading + fetch and feeds the
 * resolved {@link WikiFileHistory} in.
 */
@Component({
  selector: 'app-wiki-doc-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-doc-history.component.html',
  styleUrl: './wiki-doc-history.component.scss',
})
export class WikiDocHistoryComponent {
  readonly history = input<WikiFileHistory | null>(null);
  readonly loading = input(false);

  /** Emits the SHA of a commit whose historic content the parent should preview. */
  readonly viewRevision = output<string>();

  readonly model = computed(() => this.history()?.model ?? null);
  readonly meta = computed(() => this.history()?.metadata ?? null);
  readonly commits = computed(() => this.history()?.commits ?? []);

  /** True when there is at least one provenance fact worth a header row. */
  readonly hasProvenance = computed(() => {
    const m = this.meta();
    return !!(this.model() || m?.updatedAt || m?.reason || m?.taskKey || m?.status);
  });

  formatTime(iso: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString('en-US');
  }

  formatStat(added: number, removed: number): string {
    return `+${added} / -${removed}`;
  }
}
