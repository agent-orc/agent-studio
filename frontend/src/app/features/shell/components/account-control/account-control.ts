import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MenuComponent } from '../../../../components/menu/menu.component';
import type { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { AuthService } from '../../../../services/auth.service';
import { SessionManagerComponent } from '../../../access/lazy';

/** The signed-in account menu shared by both shell layouts. */
@Component({
  selector: 'app-account-control',
  standalone: true,
  imports: [MenuComponent, SessionManagerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './account-control.html',
  styleUrl: './account-control.scss',
})
export class AccountControlComponent {
  readonly auth = inject(AuthService);
  readonly open = signal(false);
  readonly items: MenuItem[] = [{ kind: 'row', id: 'sign-out', label: 'Sign out' }];

  select(event: MenuItemClickEvent): void {
    this.open.set(false);
    if (event.id === 'sign-out') this.auth.logout().subscribe();
  }
}
