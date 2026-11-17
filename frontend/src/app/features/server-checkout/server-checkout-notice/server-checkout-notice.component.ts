import { ChangeDetectionStrategy, Component, computed, effect, inject, input, untracked } from '@angular/core';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import { ServerCheckoutStore } from '../server-checkout.store';

/**
 * AGT-3809 - the one actionable notice for a URL project without a server
 * checkout. The full variant (Settings, create flow, board) shows progress
 * and the resulting path; the compact variant on a Ready card only offers
 * the action and its progress.
 */
@Component({
  selector: 'app-server-checkout-notice',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './server-checkout-notice.component.html',
  styleUrl: './server-checkout-notice.component.scss',
})
export class ServerCheckoutNoticeComponent {
  private readonly store = inject(ServerCheckoutStore);
  readonly projectId = input<string | null>(null);
  readonly projectName = input<string | null>(null);
  readonly compact = input(false);
  /** Settings and the create flow read the backend fact; board surfaces trust the registry. */
  readonly loadOnOpen = input(false);

  readonly project = computed(() => this.store.projectFor({ id: this.projectId(), name: this.projectName() }));
  readonly view = computed(() => {
    const view = this.store.viewFor(this.project());
    return view && this.compact() && view.state === 'ready' ? null : view;
  });

  constructor() {
    effect(() => {
      const id = this.project()?.id;
      if (id && this.loadOnOpen()) untracked(() => this.store.load(id));
    });
  }

  create(event: Event): void {
    event.stopPropagation();
    const id = this.project()?.id;
    if (id) this.store.start(id);
  }

  stop(event: Event): void {
    event.stopPropagation();
  }
}
