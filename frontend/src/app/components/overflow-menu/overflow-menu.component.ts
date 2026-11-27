import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '@studio/ui/components/menu/index';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';

/**
 * The context menu of one row or object: a "more" button that opens the
 * shared text-only `<app-menu>`. Destructive rows go last, after a separator,
 * and are marked `danger`.
 *
 *   <app-overflow-menu label="Actions for deploy-bot" [items]="actions(account)"
 *                      (itemSelect)="run($event, account)" />
 */
@Component({
  selector: 'app-overflow-menu',
  standalone: true,
  imports: [MenuComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overflow-menu.component.html',
  styleUrl: './overflow-menu.component.scss',
})
export class OverflowMenuComponent {
  readonly items = input.required<readonly MenuItem[]>();
  /** Accessible name of the trigger; names the object the actions apply to. */
  readonly label = input.required<string>();
  readonly testId = input('overflow-menu');

  /** Emits the id of the chosen row. */
  readonly itemSelect = output<string>();

  readonly open = signal(false);

  onItem(event: MenuItemClickEvent): void {
    this.open.set(false);
    this.itemSelect.emit(event.id);
  }
}
