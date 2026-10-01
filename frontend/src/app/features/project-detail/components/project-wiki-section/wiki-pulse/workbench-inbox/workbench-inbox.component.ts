import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../../components/studio-icon/studio-icon.component';
import { CopyableTaskKeyComponent } from '../../../../../../components/copyable-task-key/copyable-task-key.component';
import type {
  WikiLifecycleItem,
  WikiLifecycleState,
  WikiPulseLifecycle,
  WorkbenchCatalogue,
  WorkbenchListItem,
} from '../../../../../../models/project-docs.model';
import type { DecisionInboxItem } from '../../../../../../models/decision-card-presentation';
import { formatDateTimeUtc } from '../../../../../../services/format.util';

interface LifecycleGroup {
  state: WikiLifecycleState | 'invalid';
  label: string;
  items: WikiLifecycleItem[];
}

const GROUPS: readonly { state: WikiLifecycleState; label: string }[] = [
  { state: 'review-requested', label: 'New, wants review' },
  { state: 'in-progress', label: 'In progress' },
  { state: 'decided', label: 'Decided' },
  { state: 'documented', label: 'Documented' },
  { state: 'done', label: 'Archived' },
];

@Component({
  selector: 'app-workbench-inbox',
  standalone: true,
  imports: [StudioIconComponent, CopyableTaskKeyComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-inbox.component.html',
  styleUrl: './workbench-inbox.component.scss',
})
export class WorkbenchInboxComponent {
  readonly lifecycle = input<WikiPulseLifecycle | null>(null);
  readonly catalogue = input<WorkbenchCatalogue | null>(null);
  readonly openPage = output<WikiLifecycleItem>();
  readonly openWorkbench = output<WorkbenchListItem>();
  /**
   * AGT-2795: pending decision cards of this project. They are listed first,
   * beside the Dossier lifecycle, because each one blocks concrete work.
   */
  readonly decisionCards = input<readonly DecisionInboxItem[]>([]);
  readonly openDecisionCard = output<DecisionInboxItem>();

  readonly visible = computed(() => !!this.lifecycle() || this.decisionCards().length > 0);
  /** Header total: every row the card lists (R3, sum of visible children). */
  readonly total = computed(() => (this.lifecycle()?.count ?? 0) + this.decisionCards().length);

  readonly groups = computed<LifecycleGroup[]>(() => {
    const items = this.lifecycle()?.items ?? [];
    const groups: LifecycleGroup[] = GROUPS
      .map(group => ({ ...group, items: items.filter(item => item.valid && item.state === group.state) }))
      .filter(group => group.items.length > 0);
    const invalid = items.filter(item => !item.valid);
    if (invalid.length > 0) groups.push({ state: 'invalid', label: 'Needs metadata repair', items: invalid });
    return groups;
  });

  open(item: WikiLifecycleItem): void {
    if (item.workbenchId) {
      const workbench = this.catalogue()?.items.find(candidate => candidate.id === item.workbenchId);
      if (workbench?.valid) this.openWorkbench.emit(workbench);
      return;
    }
    if (item.valid) this.openPage.emit(item);
  }

  keyFor(item: WikiLifecycleItem): string | null {
    if (!item.workbenchId) return null;
    return this.catalogue()?.items.find(candidate => candidate.id === item.workbenchId)?.key ?? null;
  }

  decisionMeta(item: DecisionInboxItem): string {
    const due = item.dueDate ? `${item.overdue ? 'overdue since' : 'due'} ${formatDateTimeUtc(item.dueDate)}` : null;
    const blocks = item.blocks.length > 0 ? `blocks ${item.blocks.join(', ')}` : null;
    return [`decider ${item.decider}`, due, blocks].filter(Boolean).join(' · ');
  }

  stateTone(state: string): string {
    if (state === 'invalid') return 'repair';
    if (state === 'review-requested') return 'review';
    if (state === 'in-progress') return 'active';
    return 'settled';
  }

  relativeTime(iso: string): string {
    const ms = new Date(iso).getTime();
    if (Number.isNaN(ms)) return iso;
    const minutes = Math.max(0, Math.floor((Date.now() - ms) / 60_000));
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    return days < 30 ? `${days}d ago` : new Date(ms).toLocaleDateString('en-US');
  }
}
