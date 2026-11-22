import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { environmentReleaseDisplay, type EnvironmentCardView } from '../../state/deploy-area.model';
import { environmentHealthDisplay } from '../../state/deploy-status-tone';

/**
 * Health, release version and deploy age of one environment, read from the one
 * environment state projection (`buildEnvironmentCards`). The digest is detail.
 */
@Component({
  selector: 'app-environment-state',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './environment-state.html',
  styleUrl: './environment-state.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentStateComponent {
  readonly environment = input.required<EnvironmentCardView>();
  readonly now = input.required<number>();

  readonly health = computed(() => environmentHealthDisplay(this.environment().health));
  readonly release = computed(() => environmentReleaseDisplay(this.environment(), this.now()));
}
