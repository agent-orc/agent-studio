import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';

/** Creates a group and opens it, so members and roles can be added right away. */
@Component({
  selector: 'app-access-group-form',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-group-form.component.html',
  styleUrl: './access-group-form.component.scss',
})
export class AccessGroupFormComponent {
  readonly access = inject(AccessService);
  private readonly selection = inject(AccessSelectionService);
  readonly name = signal('');
  readonly description = signal('');

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.access.busy()) return;
    const group = await this.access.createGroup(name, this.description().trim(), []);
    if (!group) return;
    this.name.set('');
    this.description.set('');
    if (group.id) this.selection.open('group', group.id);
  }
}
