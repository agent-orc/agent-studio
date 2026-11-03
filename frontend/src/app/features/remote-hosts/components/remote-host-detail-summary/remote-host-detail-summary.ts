import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-remote-host-detail-summary',
  standalone: true,
  imports: [DisclosureMarkerComponent],
  templateUrl: './remote-host-detail-summary.html',
  styleUrl: './remote-host-detail-summary.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostDetailSummaryComponent {
  readonly label = input.required<string>();
  readonly summary = input.required<string>();
  readonly testId = input.required<string>();
  readonly expanded = input(false);
  readonly toggled = output<void>();
}
