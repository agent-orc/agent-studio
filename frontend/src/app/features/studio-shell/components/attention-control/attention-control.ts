import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, effect, inject, signal, viewChild } from '@angular/core';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { BatchPanelComponent } from '../../../batch-gate/batch-panel/batch-panel';
import { readinessPresentation } from '../../../acceptance-readiness';
import {
  OperatorAttentionService,
  attentionActionLabel,
  attentionAge,
  attentionCard,
  attentionTarget,
  type AttentionClassSpec,
  type OperatorAttentionItem,
} from '../../../operator-attention';
import { WorkspaceOverlaysService, type WorkspaceSettingsSection } from '../../../shell';
import { StudioPanelStateService } from '../../services/studio-panel-state.service';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import { nameWithVisibleText } from '@studio/ui/utils/accessible-name';

/**
 * The header entry that answers "what needs me" (AGT-3576). It opens one list
 * of the open decision-stream facts, grouped by class; each row names its
 * cause and offers one action that opens the card or the surface owning it.
 * The trigger count is the number of rows the list renders (R3).
 */
@Component({
  selector: 'app-attention-control',
  standalone: true,
  imports: [DialogComponent, StudioIconComponent, StatusIndicatorComponent, AppTooltipDirective, BatchPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './attention-control.html',
  styleUrl: './attention-control.scss',
})
export class AttentionControlComponent {
  readonly attention = inject(OperatorAttentionService);
  private readonly references = inject(TaskReferenceNavigationService);
  private readonly projects = inject(ProjectLookupService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly panels = inject(StudioPanelStateService);
  private readonly modalStack = inject(ModalStackService);
  private readonly injector = inject(Injector);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private releaseModal: (() => void) | null = null;

  readonly overlapPending = signal<string | null>(null);
  readonly overlapError = signal<string | null>(null);
  readonly open = signal(false);
  readonly batchProject = signal<string | null>(null);
  readonly now = signal(Date.now());
  readonly count = this.attention.count;
  readonly reviewHostDrift = computed(() => this.attention.items().some(item => item.type === 'review-host.drift'));
  readonly groups = this.attention.groups;
  readonly readinessPresentation = readinessPresentation;
  readonly triggerLabel = computed(() => {
    const count = this.count();
    if (count === 0) return 'Nothing needs you';
    const label = `${count} ${count === 1 ? 'item needs' : 'items need'} you`;
    return this.reviewHostDrift() ? `${label}; review host differs from Stable` : label;
  });
  /** Starts with the visible "Needs you <count>" (WCAG 2.5.3). */
  readonly accessibleName = computed(() => nameWithVisibleText(`Needs you ${this.count()}`, this.triggerLabel()));
  readonly closeBatch = () => this.batchProject.set(null);

  constructor() {
    this.attention.start();
    // Escape closes the list through the shared modal stack, wherever focus is.
    effect(() => {
      if (this.open() && !this.releaseModal) {
        this.releaseModal = this.modalStack.push('attention-list', () => this.close());
      } else if (!this.open() && this.releaseModal) {
        this.releaseModal();
        this.releaseModal = null;
      }
    });
    inject(DestroyRef).onDestroy(() => {
      this.attention.stop();
      this.releaseModal?.();
    });
  }

  toggle(): void {
    if (this.open()) {
      this.close();
      return;
    }
    this.now.set(Date.now());
    this.attention.setReadinessActive(true);
    this.attention.refresh();
    this.open.set(true);
    afterNextRender(() => document.querySelector<HTMLElement>('[data-testid="studio-attention-list"]')?.focus(),
      { injector: this.injector });
  }

  /** Closing by Escape or the close button returns focus to the trigger; a row action hands it on. */
  close(restoreFocus = true): void {
    if (!this.open()) return;
    this.open.set(false);
    this.attention.setReadinessActive(false);
    if (restoreFocus) this.trigger()?.nativeElement.focus();
  }

  card(item: OperatorAttentionItem): string | null {
    return attentionCard(item);
  }

  age(item: OperatorAttentionItem): string {
    return attentionAge(item.observedAt, this.now());
  }

  actionLabel(item: OperatorAttentionItem, spec: AttentionClassSpec): string {
    return attentionActionLabel(item, spec);
  }

  async runAnyway(item: OperatorAttentionItem): Promise<void> {
    if (this.overlapPending()) return;
    this.overlapPending.set(item.id);
    this.overlapError.set(null);
    try {
      await this.attention.runAnyway(item);
    } catch {
      this.overlapError.set('Run anyway could not be applied. The wait may have changed or access was refused. Refresh the list and try again.');
    } finally {
      this.overlapPending.set(null);
    }
  }

  act(item: OperatorAttentionItem, spec: AttentionClassSpec): void {
    const target = attentionTarget(item, spec);
    this.close(false);
    switch (target) {
      case 'card':
        this.references.openReferenceOrNotify(attentionCard(item)!,
          { focusDecision: item.type !== 'environment.incident-open' });
        return;
      case 'operations':
        this.openOperations(item);
        return;
      case 'dossiers':
        if (item.project && item.dossierId) {
          this.tabs.open({ kind: 'workbench', projectName: item.project, workbenchId: item.dossierId,
            title: item.title || 'Dossier', key: item.cardKey || undefined });
        } else this.tabs.open({ kind: 'workbenches', projectName: item.project });
        this.panels.noteEditorActivation();
        return;
      case 'backups':
        if (this.projects.allProjects()[0]?.displayName) {
          this.tabs.open({ kind: 'hub', projectName: this.projects.allProjects()[0].displayName, section: 'operations' });
          this.panels.noteEditorActivation();
        } else this.openActivity();
        return;
      case 'credentials':
        this.openSettings('credentials');
        return;
      case 'batch-gate':
        this.batchProject.set(item.project);
        return;
      case 'cli-usage':
        this.openSettings('caps');
        return;
      case 'hosts':
        this.openSettings('remote-hosts');
        if (item.type === 'review-host.drift') {
          const href = item.evidence?.find(link => link.label === 'Review host')?.href;
          if (href?.startsWith('/workspace/settings/environment?host=')) window.location.hash = `#${href}`;
        }
        return;
      case 'activity':
        this.openActivity();
        return;
    }
  }

  openOperations(item: OperatorAttentionItem): void {
    if (!item.project) return;
    this.close(false);
    this.tabs.open({ kind: 'hub', projectName: item.project, section: 'operations' });
    this.panels.noteEditorActivation();
  }

  openActivity(): void {
    this.close(false);
    this.tabs.open({ kind: 'feed' });
    this.panels.noteEditorActivation();
  }

  private openSettings(section: WorkspaceSettingsSection): void {
    this.overlays.open(section);
    this.tabs.open({ kind: 'workspace-settings' });
  }
}
