import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { type StudioCardLiveState } from '../../services/studio-releases.service';

@Component({
  selector: 'app-studio-release-state', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent, DatePipe],
  templateUrl: './studio-release-state.component.html',
})
export class StudioReleaseStateComponent {
  readonly state = input<StudioCardLiveState | null>(null);
}
