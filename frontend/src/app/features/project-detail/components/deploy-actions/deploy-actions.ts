import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  deployActionKey,
  deployActionLabel,
  type DeployActionOption,
  type DeployActionState,
} from '../../state/deploy-area.model';

let nextGroup = 0;

/**
 * Deploy, approval request and redeploy buttons for the options the server's
 * lifecycle returned. A refused option stays visible, disabled, with its reason.
 * The first allowed option is the group's one primary action.
 */
@Component({
  selector: 'app-deploy-actions',
  standalone: true,
  templateUrl: './deploy-actions.html',
  styleUrl: './deploy-actions.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeployActionsComponent {
  readonly options = input.required<readonly DeployActionOption[]>();
  /** Environment names by id, for the button labels. */
  readonly environmentNames = input.required<ReadonlyMap<string, string>>();
  readonly state = input.required<DeployActionState>();
  readonly groupLabel = input<string>('Deploy actions');
  readonly startAction = output<DeployActionOption>();

  private readonly group = `deploy-actions-${++nextGroup}`;

  readonly items = computed(() => {
    const primary = this.options().find(option => option.allowed);
    return this.options().map((option, index) => {
      const key = deployActionKey(option);
      const label = deployActionLabel(option, this.environmentNames().get(option.environmentId) ?? option.environmentId);
      return {
        key, option, label, primary: option === primary,
        reasonId: !option.allowed && option.reason ? `${this.group}-reason-${index}` : null,
        testId: `deploy-action-${option.action}-${option.environmentId}`,
      };
    });
  });
  readonly error = computed(() => {
    const error = this.state().error;
    return error && this.items().some(item => item.key === error.key) ? error.message : null;
  });
}
