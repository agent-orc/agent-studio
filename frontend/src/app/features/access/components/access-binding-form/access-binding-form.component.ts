import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { principalKindLabel, type IamGrant, type IamPrincipalKind } from '../../models/access.model';
import { isApiToken } from '../../models/access-view.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

interface PrincipalOption {
  value: string;
  kind: IamPrincipalKind;
  id: string;
  label: string;
  api: boolean;
}

/** Adds one role binding: a principal of any kind, a scope and a role. It then opens the principal. */
@Component({
  selector: 'app-access-binding-form',
  standalone: true,
  imports: [AccessGrantFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-binding-form.component.html',
  styleUrl: './access-binding-form.component.scss',
})
export class AccessBindingFormComponent {
  readonly access = inject(AccessService);
  private readonly selection = inject(AccessSelectionService);

  readonly principals = computed<PrincipalOption[]>(() => {
    const overview = this.access.overview();
    if (!overview) return [];
    return [
      ...overview.users.map(user => option('user', user.id, `${user.displayName} (${user.username})`)),
      ...overview.groups.map(group => option('group', group.id, group.name)),
      ...overview.serviceAccounts.filter(account => !account.disabledAt)
        .map(account => option('service-account', account.id, account.name, isApiToken(account))),
    ];
  });
  readonly principal = signal('');
  readonly selectedPrincipal = computed<PrincipalOption | null>(() =>
    this.principals().find(candidate => candidate.value === this.principal()) ?? this.principals()[0] ?? null);
  readonly grant = signal<IamGrant | null>(null);

  async add(): Promise<void> {
    const principal = this.selectedPrincipal();
    const grant = this.grant();
    if (!principal || !grant || this.access.busy()) return;
    if (await this.access.addBinding(principal.kind, principal.id, grant.role, grant.scope) === null) return;
    this.selection.open(principal.kind === 'service-account' && principal.api ? 'api-token' : principal.kind, principal.id);
  }
}

function option(kind: IamPrincipalKind, id: string, label: string, api = false): PrincipalOption {
  return { value: `${kind}:${id}`, kind, id, label: `${principalKindLabel(kind)}: ${label}`, api };
}
