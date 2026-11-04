import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ComponentRoutingResolution } from '../../../../models/task.model';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';

/**
 * Ownership routing preview in the create dialog. Resolving shows a running
 * indicator, a required question a warning indicator with the reason, and a
 * resolved route the plain preview text (AGT-3403).
 */
@Component({
  selector: 'app-routing-preview',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './routing-preview.component.html',
  styleUrl: './routing-preview.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RoutingPreviewComponent {
  readonly routing = input<ComponentRoutingResolution | null>(null);
  readonly pending = input(false);
  readonly pendingLabel = 'Resolving component ownership…';
}
