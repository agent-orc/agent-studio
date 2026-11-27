import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DataGridComponent, GridCellDirective } from '../../../../components/data-grid';
import { EmptyStateComponent } from '@studio/ui/components/empty-state/empty-state.component';
import type { MenuItem } from '@studio/ui/components/menu/index';
import { OverflowMenuComponent } from '../../../../components/overflow-menu/overflow-menu.component';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import type { AccessUser } from '../../models/access.model';
import { accessColumns, formatAccessDate, grantsOf, personStatus, personTone, type GrantView, type PersonStatus } from '../../models/access-view.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantsComponent } from '../access-grants/access-grants.component';

export interface PersonRow {
  id: string;
  name: string;
  /** The username, shown as the second line of the identity cell. */
  sub: string;
  user: AccessUser;
  grants: readonly GrantView[];
  status: PersonStatus;
  tone: StatusTone;
  signIn: string;
  menu: readonly MenuItem[];
}

/** People with their own sign-in: who they are, the roles they hold, their state, and their second factor. */
@Component({
  selector: 'app-access-people',
  standalone: true,
  imports: [AccessGrantsComponent, DataGridComponent, EmptyStateComponent, GridCellDirective, OverflowMenuComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-people.component.html',
  styleUrl: './access-people.component.scss',
})
export class AccessPeopleComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly columns = accessColumns('Person', 'Roles', 'Second factor');

  readonly rows = computed<PersonRow[]>(() => {
    const overview = this.access.overview();
    const pending = this.access.pendingInvitations();
    return (overview?.users ?? []).map(user => {
      const grants = grantsOf('user', user.id, overview, this.access.projects());
      const status = personStatus(user.disabled, pending.has(user.id), grants.length);
      return {
        id: user.id, name: user.displayName, sub: user.username, user, grants, status, tone: personTone(status),
        signIn: user.hasFactor ? `Registered${user.factorChangedAt ? `, ${formatAccessDate(user.factorChangedAt)}` : ''}` : 'None',
        menu: [
          { kind: 'row', id: 'open', label: 'Open details' },
          { kind: 'separator' },
          user.disabled ? { kind: 'row', id: 'enable', label: 'Enable' } : { kind: 'row', id: 'disable', label: 'Disable…', danger: true },
        ],
      };
    });
  });

  readonly rowId = (row: PersonRow) => row.id;
  readonly rowLabel = (row: PersonRow) => row.name;
  readonly rowTestId = (row: PersonRow) => `access-user-${row.sub}`;
  readonly muted = (row: PersonRow) => row.user.disabled;

  act(action: string, row: PersonRow): void {
    if (action === 'enable') void this.access.setUserDisabled(row.id, false);
    else this.selection.open('user', row.id, action === 'disable' ? 'disable' : null);
  }
}
