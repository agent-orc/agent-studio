import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/index';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import {
  PageContext,
  PageTaskIntent,
  pageContextKey,
  pageTypeIcon,
  pageTypeLabel,
} from '../../../../models/page-context.model';
import type { WikiHome, WikiHomeLink } from '../../../../models/project-docs.model';
import { PageContextService } from '../../../../services/page-context.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { NotificationService } from '@studio/ui/services/notification.service';

/**
 * Canonical page-head action surface. Every interactive repository page uses
 * this component so primary actions stay in the same order and position.
 */
@Component({
  selector: 'app-page-action-bar',
  standalone: true,
  imports: [AppTooltipDirective, DialogComponent, PendingButtonDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './page-action-bar.html',
  styleUrl: './page-action-bar.scss',
})
export class PageActionBarComponent {
  readonly context = input.required<PageContext>();
  readonly archived = input(false);
  readonly archivePending = input(false);
  /** Dossier host owns archive/build mutations through its two-phase decision gate. */
  readonly decisionActionsOwned = input(false);
  readonly archiveRequest = output<void>();

  private readonly pages = inject(PageContextService);
  private readonly docs = inject(ProjectDocsService);
  private readonly notifications = inject(NotificationService);

  readonly home = signal<WikiHome | null>(null);
  readonly homeLoading = signal(false);
  readonly pinDialogOpen = signal(false);
  readonly pinPending = signal(false);
  readonly pinError = signal<string | null>(null);
  readonly pinSection = signal('');
  readonly pinLabel = signal('');
  readonly pinNote = signal('');

  readonly typeLabel = computed(() => pageTypeLabel(this.context().pageType));
  readonly typeIcon = computed(() => pageTypeIcon(this.context().pageType));
  readonly extraIntent = computed<PageTaskIntent | null>(() => {
    switch (this.context().pageType) {
      case 'workbench': return 'build-feature';
      case 'incident': return 'create-follow-up';
      default: return null;
    }
  });
  readonly extraLabel = computed<string | null>(() => {
    switch (this.extraIntent()) {
      case 'build-feature': return 'Build as feature';
      case 'create-follow-up': return 'Create follow-up';
      default: return null;
    }
  });
  readonly homeSections = computed(() => this.home()?.sections ?? []);
  readonly pinnedLink = computed<WikiHomeLink | null>(() => {
    const rel = normalizePagePath(this.context().relPath);
    for (const section of this.homeSections()) {
      const link = section.links.find(candidate => normalizePagePath(candidate.relPath) === rel);
      if (link) return link;
    }
    return null;
  });
  readonly pinned = computed(() => this.pinnedLink() !== null);

  constructor() {
    effect((onCleanup) => {
      const context = this.context();
      const key = pageContextKey(context);
      this.pages.activate(context);
      onCleanup(() => this.pages.clear(key));
    });
    effect((onCleanup) => {
      const project = this.context().projectName;
      this.home.set(null);
      this.homeLoading.set(true);
      const subscription = this.docs.getWikiHome(project).subscribe({
        next: home => {
          this.home.set(home);
          this.homeLoading.set(false);
        },
        error: () => {
          this.home.set(null);
          this.homeLoading.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  createTask(intent: PageTaskIntent = 'create-task'): void {
    this.pages.createTask(this.context(), intent);
  }

  openChat(): void {
    this.pages.openChat(this.context());
  }

  togglePin(): void {
    if (this.pinPending() || this.homeLoading()) return;
    if (this.pinned()) {
      this.savePin(false);
      return;
    }
    const sections = this.homeSections();
    if (sections.length === 0) {
      this.notifications.error('No Wiki Overview sections are configured.', 'Pin to Home');
      return;
    }
    const context = this.context();
    this.pinSection.set(sections[0].title);
    this.pinLabel.set(context.title);
    this.pinNote.set(context.excerpt.slice(0, 180));
    this.pinError.set(null);
    this.pinDialogOpen.set(true);
  }

  closePinDialog(): void {
    if (!this.pinPending()) this.pinDialogOpen.set(false);
  }

  updatePinSection(event: Event): void {
    this.pinSection.set((event.target as HTMLSelectElement).value);
  }

  updatePinLabel(event: Event): void {
    this.pinLabel.set((event.target as HTMLInputElement).value);
  }

  updatePinNote(event: Event): void {
    this.pinNote.set((event.target as HTMLTextAreaElement).value);
  }

  submitPin(): void {
    if (!this.pinSection().trim() || !this.pinLabel().trim()) {
      this.pinError.set('Section and label are required.');
      return;
    }
    this.savePin(true);
  }

  private savePin(pinned: boolean): void {
    const context = this.context();
    this.pinPending.set(true);
    this.pinError.set(null);
    this.docs.setWikiHomePin(context.projectName, context.relPath, {
      pinned,
      sectionTitle: pinned ? this.pinSection() : null,
      label: pinned ? this.pinLabel() : null,
      note: pinned ? this.pinNote() : null,
    }).subscribe({
      next: () => {
        this.pinPending.set(false);
        this.pinDialogOpen.set(false);
        this.applyLocalPin(pinned);
        this.notifications.success(
          pinned ? 'Added to the shared Wiki Overview.' : 'Removed from the shared Wiki Overview.',
          'Pin to Home',
        );
      },
      error: () => {
        this.pinPending.set(false);
        const message = pinned
          ? 'Could not add this page to the Wiki Overview.'
          : 'Could not remove this page from the Wiki Overview.';
        this.pinError.set(message);
        this.notifications.error(message, 'Pin to Home');
      },
    });
  }

  private applyLocalPin(pinned: boolean): void {
    const current = this.home();
    if (!current) return;
    const rel = normalizePagePath(this.context().relPath);
    const sections = current.sections.map(section => ({
      ...section,
      links: section.links.filter(link => normalizePagePath(link.relPath) !== rel),
    }));
    if (pinned) {
      const destination = sections.find(section => section.title === this.pinSection());
      destination?.links.push({
        relPath: this.context().relPath,
        label: this.pinLabel().trim(),
        note: this.pinNote().trim() || null,
        exists: true,
      });
    }
    this.home.set({ sections });
  }
}

function normalizePagePath(relPath: string): string {
  return relPath.replaceAll('\\', '/').replace(/^\/+/, '').replace(/^docs\//i, '').toLowerCase();
}
