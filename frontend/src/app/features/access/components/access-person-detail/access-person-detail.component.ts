import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { InlineConfirmComponent } from '../../../../components/inline-confirm/inline-confirm.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { RowComponent } from '../../../../components/row/row.component';
import { SecretRevealComponent } from '../../../../components/secret-reveal/secret-reveal.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { formatAccessTime } from '../../models/access.model';
import { grantsOf, personStatus, personTone } from '../../models/access-view.model';
import { AccessSelectionService } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { AccessBindingListComponent } from '../access-binding-list/access-binding-list.component';
import { SessionManagerComponent } from '../session-manager/session-manager.component';

/**
 * Detail of one person: state and second factor, roles, sign-in actions, and
 * the one destructive action, set apart at the end and confirmed in place.
 */
@Component({
  selector: 'app-access-person-detail',
  standalone: true,
  imports: [AccessBindingListComponent, InlineConfirmComponent, PageSectionComponent, RowComponent, SecretRevealComponent, SessionManagerComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-person-detail.component.html',
  styleUrl: './access-person-detail.component.scss',
})
export class AccessPersonDetailComponent {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  readonly userId = input.required<string>();
  readonly temporaryPassword = signal<string | null>(null);
  readonly time = formatAccessTime;

  readonly user = computed(() => this.access.overview()?.users.find(candidate => candidate.id === this.userId()) ?? null);
  readonly status = computed(() => {
    const user = this.user();
    if (!user) return null;
    const grants = grantsOf('user', user.id, this.access.overview(), this.access.projects()).length;
    const label = personStatus(user.disabled, this.access.pendingInvitations().has(user.id), grants);
    return { label, tone: personTone(label) };
  });

  constructor() {
    // A one-time password belongs to one person and one signed-in view: another row or a refusal drops it.
    effect(() => {
      this.userId();
      untracked(() => this.temporaryPassword.set(null));
    });
    effect(() => {
      if (this.access.state() === 'denied') this.temporaryPassword.set(null);
    });
  }

  async resetPassword(): Promise<void> {
    if (this.access.busy()) return;
    const reset = await this.access.resetPassword(this.userId());
    if (reset) this.temporaryPassword.set(reset.temporaryPassword);
  }

  async setDisabled(disabled: boolean): Promise<void> {
    if (this.access.busy()) return;
    await this.access.setUserDisabled(this.userId(), disabled);
  }
}
