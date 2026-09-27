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
