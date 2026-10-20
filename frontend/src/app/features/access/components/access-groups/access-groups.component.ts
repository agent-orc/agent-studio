import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { AccessGroup, AccessUser } from '../../models/access.model';
import { AccessService } from '../../services/access.service';

/** Groups bundle users; their role bindings apply to every member. */
@Component({
  selector: 'app-access-groups',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-groups.component.html',
  styleUrl: './access-groups.component.scss',
})
export class AccessGroupsComponent {
  readonly access = inject(AccessService);
  readonly name = signal('');
  readonly description = signal('');
  readonly users = computed(() => this.access.overview()?.users ?? []);
  readonly groups = computed(() => this.access.overview()?.groups ?? []);

  members(group: AccessGroup): AccessUser[] {
    return group.members
      .map(id => this.users().find(user => user.id === id))
      .filter((user): user is AccessUser => !!user);
  }

  candidates(group: AccessGroup): AccessUser[] {
    return this.users().filter(user => !group.members.includes(user.id));
  }

  bindingLabel(group: AccessGroup): string {
    const count = (this.access.overview()?.bindings ?? [])
      .filter(binding => binding.principalKind === 'group' && binding.principalId === group.id).length;
    return `${count} ${count === 1 ? 'binding' : 'bindings'}`;
  }

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.access.busy()) return;
    if (await this.access.createGroup(name, this.description().trim(), [])) {
      this.name.set('');
      this.description.set('');
    }
  }

  async addMember(group: AccessGroup, userId: string): Promise<void> {
    if (!userId || this.access.busy()) return;
    await this.access.updateGroupMembers(group.id, [...group.members, userId]);
  }

  async removeMember(group: AccessGroup, userId: string): Promise<void> {
    if (this.access.busy()) return;
    await this.access.updateGroupMembers(group.id, group.members.filter(member => member !== userId));
  }

  async remove(group: AccessGroup): Promise<void> {
    if (this.access.busy()) return;
    await this.access.deleteGroup(group.id);
  }
}
