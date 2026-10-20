import { ChangeDetectionStrategy, Component, computed, effect, input, linkedSignal, output, signal } from '@angular/core';
import {
  bindableRoles,
  type AccessProject,
  type IamGrant,
  type IamPrincipalKind,
  type IamRole,
  type IamScopeKind,
} from '../../models/access.model';

/**
 * Role and scope picker for one grant. Offers only the roles the principal
 * kind may hold in the chosen scope kind and emits a complete grant or null.
 */
@Component({
  selector: 'app-access-grant-form',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-grant-form.component.html',
  styleUrl: './access-grant-form.component.scss',
})
export class AccessGrantFormComponent {
  readonly roles = input.required<readonly IamRole[]>();
  readonly projects = input<readonly AccessProject[]>([]);
  readonly principalKind = input<IamPrincipalKind>('user');
  readonly grantChange = output<IamGrant | null>();

  readonly scopeKind = signal<IamScopeKind>('project');
  readonly project = signal('');
  readonly environment = signal('production');

  /** Environment scopes name the environment kind of the project's declarations, never an environment id. */
  readonly environments: readonly { key: string; label: string }[] = [
    { key: 'production', label: 'Production' },
    { key: 'stage', label: 'Stage' },
    { key: 'other', label: 'Other' },
  ];

  readonly scopeKinds: readonly { kind: IamScopeKind; label: string }[] = [
    { kind: 'workspace', label: 'Workspace' },
    { kind: 'project', label: 'Project' },
    { kind: 'environment', label: 'Environment' },
  ];

  readonly availableRoles = computed(() => bindableRoles(this.roles(), this.principalKind(), this.scopeKind()));
  /** Keeps the chosen role while it stays bindable, else falls back to the first one. */
  readonly role = linkedSignal<readonly IamRole[], string>({
    source: this.availableRoles,
    computation: (roles, previous) =>
      previous && roles.some(role => role.id === previous.value) ? previous.value : roles[0]?.id ?? '',
  });

  readonly grant = computed<IamGrant | null>(() => {
    const role = this.availableRoles().find(candidate => candidate.id === this.role());
    if (!role) return null;
    const kind = this.scopeKind();
    if (kind === 'workspace') return { role: role.id, scope: { kind } };
    const project = this.project().trim();
    if (!project) return null;
    if (kind === 'project') return { role: role.id, scope: { kind, project } };
    const environment = this.environment().trim();
    return environment ? { role: role.id, scope: { kind, project, environment } } : null;
  });

  constructor() {
    effect(() => this.grantChange.emit(this.grant()));
  }

  roleDescription(): string {
    return this.availableRoles().find(role => role.id === this.role())?.description ?? '';
  }

  setScopeKind(value: string): void {
    if (value === 'workspace' || value === 'project' || value === 'environment') this.scopeKind.set(value);
  }

  reset(): void {
    this.project.set('');
    this.environment.set('production');
  }
}
