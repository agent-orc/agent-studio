import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChipComponent } from '../../../../components/chip/chip.component';
import { DataGridComponent, GridCellDirective, type GridColumn } from '../../../../components/data-grid';
import { EmptyStateComponent } from '@studio/ui/components/empty-state/empty-state.component';
import type { MenuItem } from '@studio/ui/components/menu/index';
import { OverflowMenuComponent } from '../../../../components/overflow-menu/overflow-menu.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { groupBindingsByScope, principalKindLabel, principalName, scopeLabel, type IamScopeKind, type RoleBinding } from '../../models/access.model';
import { accessColumns, isApiToken, roleName } from '../../models/access-view.model';
import { AccessSelectionService, type AccessDetailKind } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';

export interface BindingRow {
  id: string;
  name: string;
  /** The kind of principal, shown as the second line of the identity cell. */
  sub: string;
  role: string;
  owner: boolean;
  meta: string;
  binding: RoleBinding;
  /** The detail this principal opens in; an API token has its own section. */
  detail: AccessDetailKind;
  menu: readonly MenuItem[];
}

interface ScopeView {
  key: string;
  title: string;
  kind: string;
  rows: readonly BindingRow[];
}

const KIND: Record<IamScopeKind, string> = { workspace: 'The whole workspace.', project: 'One project.', environment: 'One environment of a project.' };
const MENU: readonly MenuItem[] = [
  { kind: 'row', id: 'open', label: 'Open principal' },
  { kind: 'separator' },
  { kind: 'row', id: 'remove', label: 'Remove role…', danger: true },
];

/**
 * Role bindings by scope: for the workspace, each project and each
 * environment, who holds which role there. A row opens its principal, where
 * roles are added and removed.
 */
@Component({
  selector: 'app-access-bindings',
  standalone: true,
  imports: [ChipComponent, DataGridComponent, EmptyStateComponent, GridCellDirective, OverflowMenuComponent, PageSectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-bindings.component.html',
  styleUrl: './access-bindings.component.scss',
})
export class AccessBindingsComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly columns: GridColumn[] = accessColumns('Principal', 'Role', 'Source', null);

  readonly scopes = computed<ScopeView[]>(() => {
    const overview = this.access.overview();
    const projects = this.access.projects();
    return groupBindingsByScope(overview?.bindings ?? [], projects).map(group => ({
      key: group.key,
      title: scopeLabel(group.scope, projects),
      kind: KIND[group.scope.kind],
      rows: group.bindings.map(binding => {
        const account = binding.principalKind === 'service-account'
          ? overview?.serviceAccounts.find(candidate => candidate.id === binding.principalId) : undefined;
        return {
          id: binding.id, name: principalName(binding.principalKind, binding.principalId, overview),
          sub: account && isApiToken(account) ? 'API token' : principalKindLabel(binding.principalKind),
          role: roleName(overview, binding.role), owner: binding.role === 'owner', meta: binding.source, binding,
          detail: binding.principalKind === 'service-account' ? (account && isApiToken(account) ? 'api-token' : 'service-account') : binding.principalKind,
          menu: MENU,
        };
      }),
    }));
  });

  readonly rowId = (row: BindingRow) => row.id;
  readonly rowLabel = (row: BindingRow) => row.name;
  readonly rowTestId = (row: BindingRow) => `access-binding-${row.id}`;

  /** The detail of the row's principal; "Remove role" opens its confirmation there. */
  open(row: BindingRow, remove = false): void {
    this.selection.open(row.detail, row.binding.principalId, remove ? `remove-binding:${row.id}` : null);
  }
}
