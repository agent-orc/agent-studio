import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ServerCheckoutStore } from '../server-checkout.store';
import { ServerCheckoutNoticeComponent } from '../server-checkout-notice/server-checkout-notice.component';

/** AGT-3809 - above the board, one notice per in-scope project without a server checkout. */
@Component({
  selector: 'app-server-checkout-board-notice',
  standalone: true,
  imports: [ServerCheckoutNoticeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './server-checkout-board-notice.component.html',
  styleUrl: './server-checkout-board-notice.component.scss',
})
export class ServerCheckoutBoardNoticeComponent {
  private readonly store = inject(ServerCheckoutStore);
  /** The project-scoped board's project name; null for the all-projects board. */
  readonly projectScope = input<string | null>(null);

  readonly projects = computed(() => {
    const scope = this.projectScope()?.toLowerCase();
    return this.store.noticeProjects().filter(project => !scope || project.displayName.toLowerCase() === scope);
  });
}
