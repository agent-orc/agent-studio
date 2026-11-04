import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-detail-load-error',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './detail-load-error.component.html',
  styleUrl: './detail-load-error.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DetailLoadErrorComponent {
  readonly taskLabel = input.required<string>();
  readonly message = input.required<string>();
  readonly retry = output<void>();
}
