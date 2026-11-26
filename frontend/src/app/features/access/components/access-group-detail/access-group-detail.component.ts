import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import type { AccessUser } from '../../models/access.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessBindingListComponent } from '../access-binding-list/access-binding-list.component';

/** Detail of one group: its members, the roles it grants them, and deletion, set apart and confirmed in place. */
@Component({
  selector: 'app-access-group-detail',
  standalone: true,
  imports: [AccessBindingListComponent, InlineConfirmComponent, PageSectionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-group-detail.component.html',
  styleUrl: './access-group-detail.component.scss',
})
export class AccessGroupDetailComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly groupId = input.required<string>();

  readonly group = computed(() => this.access.overview()?.groups.find(candidate => candidate.id === this.groupId()) ?? null);
  private readonly users = computed(() => this.access.overview()?.users ?? []);
  readonly members = computed<AccessUser[]>(() => (this.group()?.members ?? [])
    .map(id => this.users().find(user => user.id === id))
    .filter((user): user is AccessUser => !!user));
  readonly candidates = computed(() => this.users().filter(user => !this.group()?.members.includes(user.id)));

  async addMember(userId: string): Promise<void> {
    const group = this.group();
    if (!group || !userId || this.access.busy()) return;
    await this.access.updateGroupMembers(group.id, [...group.members, userId]);
  }

  async removeMember(userId: string): Promise<void> {
    const group = this.group();
    if (!group || this.access.busy()) return;
    await this.access.updateGroupMembers(group.id, group.members.filter(member => member !== userId));
  }

  async remove(): Promise<void> {
    if (this.access.busy()) return;
    if (await this.access.deleteGroup(this.groupId()) !== null) this.selection.close();
  }
}
