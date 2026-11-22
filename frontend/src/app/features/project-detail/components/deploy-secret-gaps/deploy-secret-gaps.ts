import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { secretReasonLabel, type EnvironmentSecretReadiness } from '../../state/deploy-area.model';

/**
 * F14: the secrets a deploy into one environment would miss, with the action
 * that opens the binding panel on that environment. Renders nothing when the
 * environment is ready.
 */
@Component({
  selector: 'app-deploy-secret-gaps',
  standalone: true,
  templateUrl: './deploy-secret-gaps.html',
  styleUrl: './deploy-secret-gaps.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DeploySecretGapsComponent {
  readonly readiness = input.required<EnvironmentSecretReadiness | null | undefined>();
  /** Prefix the environment name, where several environments are listed together. */
  readonly named = input(false);
  readonly bind = output<string>();

  readonly gaps = computed(() => {
    const readiness = this.readiness();
    return readiness && !readiness.ready && readiness.missing.length ? readiness : null;
  });
  readonly heading = computed(() => {
    const gaps = this.gaps();
    if (!gaps) return '';
    const count = gaps.missing.length;
    const subject = `${count} ${count === 1 ? 'secret' : 'secrets'} missing`;
    return this.named() ? `${gaps.environmentName}: ${subject}` : `${subject} before deploy`;
  });
  readonly reason = secretReasonLabel;
}
