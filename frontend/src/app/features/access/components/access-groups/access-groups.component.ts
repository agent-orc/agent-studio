import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DataGridComponent, GridCellDirective } from '../../../../components/data-grid';
import { EmptyStateComponent } from '@studio/ui/components/empty-state/empty-state.component';
import type { MenuItem } from '@studio/ui/components/menu/index';
import { OverflowMenuComponent } from '../../../../components/overflow-menu/overflow-menu.component';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { accessColumns, grantsOf, type GrantView } from '../../models/access-view.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantsComponent } from '../access-grants/access-grants.component';

export interface GroupRow {
  id: string;
  name: string;
  sub: string;
  grants: readonly GrantView[];
  status: 'Active' | 'No members' | 'No role';
  tone: StatusTone;
  /** Names of the members: the fourth column of the group grid. */
  meta: string;
  menu: readonly MenuItem[];
}

const MENU: readonly MenuItem[] = [
  { kind: 'row', id: 'open', label: 'Open details' },
  { kind: 'separator' },
  { kind: 'row', id: 'delete', label: 'Delete…', danger: true },
];

/** Groups bundle people; a role bound to the group applies to every member. */
@Component({
  selector: 'app-access-groups',
  standalone: true,
  imports: [AccessGrantsComponent, DataGridComponent, EmptyStateComponent, GridCellDirective, OverflowMenuComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-groups.component.html',
  styleUrl: './access-groups.component.scss',
})
export class AccessGroupsComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly columns = accessColumns('Group', 'Roles', 'Members');

  readonly rows = computed<GroupRow[]>(() => {
    const overview = this.access.overview();
    const names = new Map((overview?.users ?? []).map(user => [user.id, user.displayName]));
    return (overview?.groups ?? []).map(group => {
      const grants = grantsOf('group', group.id, overview, this.access.projects());
      const members = group.members.map(id => names.get(id)).filter((name): name is string => !!name);
      // A group grants something only with both a member and a role.
      const status = members.length === 0 ? 'No members' : grants.length === 0 ? 'No role' : 'Active';
      return {
        id: group.id, name: group.name, sub: group.description ?? '', grants, status, tone: status === 'Active' ? 'success' : 'neutral',
        meta: members.join(', '), menu: MENU,
      };
    });
  });

  readonly rowId = (row: GroupRow) => row.id;
  readonly rowLabel = (row: GroupRow) => row.name;
  readonly rowTestId = (row: GroupRow) => `access-group-${row.id}`;

  act(action: string, row: GroupRow): void {
    this.selection.open('group', row.id, action === 'delete' ? 'delete' : null);
  }
}
