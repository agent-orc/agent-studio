import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { WikiClassification } from '../../../../../models/project-docs.model';
import {
  PageContext,
  derivePageType,
  pageExcerpt,
} from '../../../../../models/page-context.model';
import { NotificationService } from '@studio/ui/services/notification.service';
import { ProjectDocsService } from '../../../../../services/project-docs.service';
import { PageActionBarComponent } from '../../page-action-bar/page-action-bar';

/** Adapts the open Wiki reader state to the shared page-head action contract. */
@Component({
  selector: 'app-wiki-page-actions',
  standalone: true,
  imports: [PageActionBarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-page-actions.html',
  styleUrl: './wiki-page-actions.scss',
})
export class WikiPageActionsComponent {
  readonly projectName = input.required<string>();
  readonly relPath = input.required<string>();
  readonly title = input.required<string>();
  readonly content = input.required<string>();
  readonly classification = input<WikiClassification | null>(null);
  readonly registeredWorkbenchPaths = input<ReadonlySet<string>>(new Set());
  readonly archiveCompleted = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly notifications = inject(NotificationService);
  private readonly archivedOverrides = signal<ReadonlySet<string>>(new Set());
  readonly archiveBusy = signal(false);

  readonly context = computed<PageContext>(() => ({
    projectName: this.projectName(),
    relPath: this.relPath(),
    title: this.title(),
    pageType: derivePageType(
      this.relPath(),
      this.classification(),
      this.registeredWorkbenchPaths(),
    ),
    excerpt: pageExcerpt(this.content(), this.title()),
  }));

  readonly archived = computed(() =>
    this.archivedOverrides().has(this.relPath())
    || this.classification()?.status?.toLowerCase() === 'archived');

  /**
   * Everything inside a registered Dossier folder settles through the
   * Dossier's own two-phase decision gate. Wiki classification writes a
   * `.meta.json` sidecar that a Dossier's visibility never reads, so the
   * backend refuses it with 409 - the UI must not offer the path at all.
   */
  readonly decisionActionsOwned = computed(() => {
    const rel = normalizeWikiPath(this.relPath());
    for (const entryPath of this.registeredWorkbenchPaths()) {
      const folder = workbenchFolder(normalizeWikiPath(entryPath));
      if (folder && (rel === folder || rel.startsWith(`${folder}/`))) return true;
    }
    return false;
  });

  archivePage(): void {
    const context = this.context();
    if (this.archiveBusy() || this.archived() || this.decisionActionsOwned()) return;
    this.archivedOverrides.update(current => new Set(current).add(context.relPath));
    this.archiveBusy.set(true);
    this.docs.setWikiClassification(context.projectName, context.relPath, 'archived').subscribe({
      next: () => {
        this.archiveBusy.set(false);
        this.notifications.success(`Archived ${context.relPath}.`, 'Page classification');
        this.archiveCompleted.emit();
      },
      error: () => {
        this.archivedOverrides.update(current => {
          const next = new Set(current);
          next.delete(context.relPath);
          return next;
        });
        this.archiveBusy.set(false);
        this.notifications.error(`Could not archive ${context.relPath}.`, 'Page classification');
      },
    });
  }
}

/** Repo-relative, case-insensitive comparison form; `docs/` is optional. */
function normalizeWikiPath(relPath: string): string {
  return (relPath ?? '').replaceAll('\\', '/').replace(/^\/+/, '').replace(/^docs\//i, '').toLowerCase();
}

/** The folder that owns a Dossier entry point, or null for a bare file. */
function workbenchFolder(entryPath: string): string | null {
  const cut = entryPath.lastIndexOf('/');
  return cut > 0 ? entryPath.slice(0, cut) : null;
}
