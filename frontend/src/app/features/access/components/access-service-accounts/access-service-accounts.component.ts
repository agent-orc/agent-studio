import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { DataGridComponent, GridCellDirective } from '../../../../components/data-grid';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import type { MenuItem } from '../../../../components/menu';
import { OverflowMenuComponent } from '../../../../components/overflow-menu/overflow-menu.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { principalName, type ServiceAccount } from '../../models/access.model';
import { accessColumns, grantsOf, isApiToken, tokenSummary, type GrantView } from '../../models/access-view.model';
import { isOperatorAgentAccount } from '../../models/operator-agent.model';
import { ACCESS_CLOCK } from '../../services/access-clock';
import { AccessSelectionService, type AccessDetailKind } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantsComponent } from '../access-grants/access-grants.component';

export interface AccountRow {
  id: string;
  name: string;
  sub: string;
  account: ServiceAccount;
  grants: readonly GrantView[];
  disabled: boolean;
  tokens: string;
  menu: readonly MenuItem[];
}

/**
 * Non-human principals in one grid. `kind="service-account"` lists the
 * accounts scripts and jobs use; `kind="api-token"` lists API tokens
 * (AGT-3186), which are service accounts with one token bound to its creator.
 * Operator agents are service accounts too, but live in the operator panel.
 */
@Component({
  selector: 'app-access-service-accounts',
  standalone: true,
  imports: [AccessGrantsComponent, DataGridComponent, EmptyStateComponent, GridCellDirective, OverflowMenuComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-service-accounts.component.html',
  styleUrl: './access-service-accounts.component.scss',
})
export class AccessServiceAccountsComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly now = inject(ACCESS_CLOCK);
  readonly kind = input<Extract<AccessDetailKind, 'service-account' | 'api-token'>>('service-account');

  readonly api = computed(() => this.kind() === 'api-token');
  readonly columns = computed(() => this.api()
    ? accessColumns('API token', 'Role', 'Token')
    : accessColumns('Service account', 'Roles', 'Tokens'));

  readonly rows = computed<AccountRow[]>(() => {
    const overview = this.access.overview();
    const api = this.api();
    return (overview?.serviceAccounts ?? [])
      .filter(account => !isOperatorAgentAccount(account) && isApiToken(account) === api)
      .map(account => {
        const disabled = !!account.disabledAt;
        const creator = account.createdBy ? principalName('user', account.createdBy, overview) : '';
        return {
          id: account.id, name: account.name, account, disabled,
          sub: api && creator ? `Created by ${creator}` : account.description ?? '',
          grants: grantsOf('service-account', account.id, overview, this.access.projects()),
          tokens: disabled ? '' : tokenSummary(account, this.now()),
          menu: [
            { kind: 'row', id: 'open', label: 'Open details' },
            ...(disabled ? [] : [{ kind: 'separator' }, { kind: 'row', id: 'disable', label: 'Disable…', danger: true }] as MenuItem[]),
          ],
        };
      });
  });

  readonly rowId = (row: AccountRow) => row.id;
  readonly rowLabel = (row: AccountRow) => row.name;
  readonly rowTestId = (row: AccountRow) => `access-service-account-${row.id}`;
  readonly muted = (row: AccountRow) => row.disabled;

  act(action: string, row: AccountRow): void {
    this.selection.open(this.kind(), row.id, action === 'disable' ? 'disable' : null);
  }
}
