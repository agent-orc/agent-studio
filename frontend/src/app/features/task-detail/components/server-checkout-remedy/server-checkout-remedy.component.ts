import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';

import { PendingButtonDirective } from '../../../../components/async-feedback';
import { laneName } from '../../../../models/lane-presentation';
import type { TaskInfo } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { studioProjectSlug } from '../../../../services/studio-project-slug.util';
import { serverErrorMessage } from '../../../../utils/server-error-message';
import { serverCheckoutReady } from '../../state/triage-actions.model';

/**
 * Remedy for an integration that cannot run until the Studio server has a project checkout.
 * Once the checkout exists (AGT-3810) it offers Retry integration, which merges the same
 * reviewed delivery again instead of a new coding run.
 */
@Component({
  selector: 'app-server-checkout-remedy',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PendingButtonDirective],
  templateUrl: './server-checkout-remedy.component.html',
  styleUrl: './server-checkout-remedy.component.scss',
})
export class ServerCheckoutRemedyComponent {
  readonly info = input.required<TaskInfo>();
  readonly mutationsBlocked = input(false);
  /** The backend found the checkout, so the same delivery can integrate now. */
  readonly checkoutReady = computed(() => serverCheckoutReady(this.info()));
  readonly settingsHref = computed(() => `/#/projects/${studioProjectSlug(this.info().projectName)}/settings`);
  readonly retrying = signal(false);
  readonly outcome = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  private readonly tasks = inject(TaskService);

  retry(): void {
    const { id: jobId, watchPath } = this.info();
    if (this.retrying() || this.mutationsBlocked()) return;
    this.retrying.set(true);
    this.outcome.set(null);
    this.error.set(null);
    this.tasks.retryIntegration(jobId, watchPath || undefined).subscribe({
      next: response => {
        this.retrying.set(false);
        if (response.status === 'integrated') {
          this.outcome.set(`Integrated into ${response.integrationBranch ?? 'the integration branch'} without a new coding run.`
            + (response.movedTo ? ` The card is back in ${laneName(response.movedTo)} for acceptance.` : ''));
        } else {
          this.error.set(`The integration retry failed again: ${response.reason}`);
        }
        this.tasks.refresh(true);
      },
      error: (failure: unknown) => {
        this.retrying.set(false);
        this.error.set(serverErrorMessage(failure, 'Retry integration failed.'));
      },
    });
  }
}
