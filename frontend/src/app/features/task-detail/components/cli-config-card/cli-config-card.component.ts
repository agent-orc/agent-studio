import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { CliSettings } from '../../../../models/task.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** CLI path availability: found is success, not found is danger (AGT-3403). */
export function cliAvailabilityTone(available: boolean | null | undefined): StatusTone {
  return available ? 'success' : 'danger';
}

/** GitHub token presence: configured is success, missing needs an operator step (AGT-3403). */
export function cliTokenTone(hasToken: boolean | null | undefined): StatusTone {
  return hasToken ? 'success' : 'warning';
}

/**
 * Inline CLI configuration card surfaced when the active CLI is
 * unreachable or the user clicks "configure CLI" from an error
 * dialog. Exposes path test + save and GitHub-token save. All API
 * calls and state mutations are owned by the parent — this component
 * only renders + emits intent.
 */
@Component({
  selector: 'app-cli-config-card',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cli-config-card.component.html'
})
export class CliConfigCardComponent {
  readonly status = input<CliSettings | null>(null);
  readonly testResult = input<CliSettings | null>(null);
  readonly pathDraft = input<string>('');
  readonly tokenDraft = input<string>('');
  readonly testing = input(false);
  readonly tokenSaving = input(false);

  readonly closeRequest = output<void>();
  readonly pathDraftChange = output<string>();
  readonly tokenDraftChange = output<string>();
  readonly testPath = output<void>();
  readonly savePath = output<void>();
  readonly saveToken = output<void>();

  // Local-only: whether the token input is in clear-text mode.
  readonly showToken = signal(false);
  readonly availabilityTone = cliAvailabilityTone;
  readonly tokenTone = cliTokenTone;

  availabilityLabel(status: CliSettings): string {
    return status.available ? `${status.version ?? ''}, ${status.path}` : `Not found at: ${status.path}`;
  }

  testResultLabel(result: CliSettings): string {
    return result.available ? `Found: ${result.version ?? ''}` : result.version || 'Not found at this path';
  }
}
