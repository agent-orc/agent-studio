import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { EffectiveModelSelection } from '../../../../quota';
import { StatusIndicatorComponent } from '../../../../../components/status-indicator/status-indicator.component';
import { effectiveRouteView } from './pipeline-step-effective-route.util';

/** Effective launch route of one LLM step, shown only when it contradicts the configured route. */
@Component({
  selector: 'app-pipeline-step-effective-route',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './pipeline-step-effective-route.component.html',
  styleUrl: './pipeline-step-effective-route.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.is-shown]': 'view() !== null' },
})
export class PipelineStepEffectiveRouteComponent {
  readonly stepId = input.required<string>();
  readonly route = input<EffectiveModelSelection | null | undefined>(null);
  readonly configuredCli = input('');
  readonly configuredModel = input('');

  readonly view = computed(() =>
    effectiveRouteView(this.route(), this.configuredCli(), this.configuredModel()));
}
