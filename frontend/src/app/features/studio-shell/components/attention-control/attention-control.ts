import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, effect, inject, signal, viewChild } from '@angular/core';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';
import { ModalStackService } from '@studio/ui/services/modal-stack.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { NowTickService } from '../../../../services/now-tick.service';
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
  private readonly clock = inject(NowTickService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  private readonly tabs = inject(StudioTabStateService);
  private readonly panels = inject(StudioPanelStateService);
  private readonly modalStack = inject(ModalStackService);
  private readonly injector = inject(Injector);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private cancelBackupHandoff: (() => void) | null = null;
  private releaseModal: (() => void) | null = null;

  readonly overlapPending = signal<string | null>(null);
  readonly overlapError = signal<string | null>(null);
  readonly open = signal(false);
  readonly batchProject = signal<string | null>(null);
  private readonly openedAt = signal(Date.now());
  // Dynamic dependencies detach the shared clock while the list is closed.
  readonly now = computed(() => this.open() ? Math.max(this.openedAt(), this.clock.now()) : this.openedAt());
  readonly count = this.attention.count;
  readonly reviewHostDrift = computed(() => this.attention.items().some(item => item.type === 'review-host.drift'));
  readonly groups = this.attention.groups;
  readonly readinessPresentation = readinessPresentation;
  readonly triggerLabel = computed(() => {
    const count = this.count();
    const coverage = this.attention.coverageLabel();
    if (count === 0) return coverage || 'Nothing needs you';
    const label = `${count} ${count === 1 ? 'item needs' : 'items need'} you`;
    const detail = this.reviewHostDrift() ? `${label}; review host differs from Stable` : label;
    return coverage ? `${detail}; ${coverage}` : detail;
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
      this.cancelBackupHandoff?.();
      this.releaseModal?.();
    });
  }

  toggle(): void {
    if (this.open()) {
      this.close();
      return;
    }
    this.cancelBackupHandoff?.();
    this.openedAt.set(Date.now());
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
        this.openSettings('operations');
        this.revealBackups();
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

  /** Operations and its backup block are deferred; wait for their DOM, with bounded cleanup. */
  private revealBackups(): void {
    this.cancelBackupHandoff?.();
    const reveal = (): boolean => {
      const panel = document.querySelector<HTMLElement>('[data-testid="operations-backups"]');
      if (!panel) return false;
      panel.scrollIntoView({ block: 'start' });
      // Security loads above this block; reveal it again after that layout settles.
      return !panel.closest('[data-testid="workspace-operations"]')?.querySelector('app-loading-surface');
    };
    if (reveal()) return;
    const observer = new MutationObserver(() => {
      if (reveal()) this.cancelBackupHandoff?.();
    });
    const timeout = setTimeout(() => this.cancelBackupHandoff?.(), 15_000);
    this.cancelBackupHandoff = () => {
      observer.disconnect();
      clearTimeout(timeout);
      this.cancelBackupHandoff = null;
    };
    observer.observe(document.body, { childList: true, subtree: true });
  }

  private openSettings(section: WorkspaceSettingsSection): void {
    this.overlays.open(section);
    this.tabs.open({ kind: 'workspace-settings' });
  }
}
