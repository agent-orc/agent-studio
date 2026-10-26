import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  groupBindingsByScope,
  principalKindLabel,
  principalName,
  scopeLabel,
  type IamGrant,
  type IamPrincipalKind,
  type RoleBinding,
} from '../../models/access.model';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

interface PrincipalOption {
  value: string;
  kind: IamPrincipalKind;
  id: string;
  label: string;
}

/** Role bindings per scope, with removal and a form to add one. */
@Component({
  selector: 'app-access-bindings',
  standalone: true,
  imports: [AccessGrantFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-bindings.component.html',
  styleUrl: './access-bindings.component.scss',
})
export class AccessBindingsComponent {
  readonly access = inject(AccessService);

  readonly groups = computed(() => groupBindingsByScope(this.access.overview()?.bindings ?? [], this.access.projects()));
  readonly principals = computed<PrincipalOption[]>(() => {
    const overview = this.access.overview();
    if (!overview) return [];
    return [
      ...overview.users.map(user => option('user', user.id, `${user.displayName} (${user.username})`)),
      ...overview.groups.map(group => option('group', group.id, group.name)),
      ...overview.serviceAccounts.filter(account => !account.disabledAt)
        .map(account => option('service-account', account.id, account.name)),
    ];
  });
  readonly principal = signal('');
  readonly selectedPrincipal = computed<PrincipalOption | null>(() =>
    this.principals().find(candidate => candidate.value === this.principal()) ?? this.principals()[0] ?? null);
  readonly grant = signal<IamGrant | null>(null);

  scopeTitle(binding: RoleBinding): string {
    return scopeLabel(binding.scope, this.access.projects());
  }

  principalLabel(binding: RoleBinding): string {
    return principalName(binding.principalKind, binding.principalId, this.access.overview());
  }

  kindLabel(kind: IamPrincipalKind): string {
    return principalKindLabel(kind);
  }

  roleName(id: string): string {
    return this.access.overview()?.roles.find(role => role.id === id)?.displayName ?? id;
  }

  async add(): Promise<void> {
    const principal = this.selectedPrincipal();
    const grant = this.grant();
    if (!principal || !grant || this.access.busy()) return;
    await this.access.addBinding(principal.kind, principal.id, grant.role, grant.scope);
  }

  async remove(binding: RoleBinding): Promise<void> {
    if (this.access.busy()) return;
    await this.access.removeBinding(binding.id);
  }
}

function option(kind: IamPrincipalKind, id: string, label: string): PrincipalOption {
  return { value: `${kind}:${id}`, kind, id, label: `${principalKindLabel(kind)}: ${label}` };
}
