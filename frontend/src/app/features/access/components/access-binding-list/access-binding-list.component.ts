import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { ChipComponent } from '../../../../components/chip/chip.component';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { principalName, type IamGrant, type IamPrincipalKind } from '../../models/access.model';
import { grantsOf, type GrantView } from '../../models/access-view.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessGrantFormComponent } from '../access-grant-form/access-grant-form.component';

/**
 * The roles of one principal in the detail area: each binding with its scope
 * and source, removal confirmed in place, and a form to add one. Roles a
 * person holds through a group are listed but changed on the group.
 */
@Component({
  selector: 'app-access-binding-list',
  standalone: true,
  imports: [AccessGrantFormComponent, ChipComponent, InlineConfirmComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-binding-list.component.html',
  styleUrl: './access-binding-list.component.scss',
})
export class AccessBindingListComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly principalKind = input.required<IamPrincipalKind>();
  readonly principalId = input.required<string>();
  /** A disabled principal keeps its bindings on record but takes no new one. */
  readonly canAdd = input(true);
  readonly emptyText = input('No role bindings.');

  readonly grant = signal<IamGrant | null>(null);
  readonly grants = computed(() => grantsOf(this.principalKind(), this.principalId(), this.access.overview(), this.access.projects()));
  readonly name = computed(() => principalName(this.principalKind(), this.principalId(), this.access.overview()));

  armKey(grant: GrantView): string {
    return `remove-binding:${grant.id}`;
  }

  async add(): Promise<void> {
    const grant = this.grant();
    if (!grant || this.access.busy()) return;
    await this.access.addBinding(this.principalKind(), this.principalId(), grant.role, grant.scope);
  }

  async remove(grant: GrantView): Promise<void> {
    if (this.access.busy()) return;
    await this.access.removeBinding(grant.id);
  }
}
