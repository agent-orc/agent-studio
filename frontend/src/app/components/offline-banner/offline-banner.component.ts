import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';
import { ConnectionStatusService } from '../../services/connection-status.service';

/**
 * App-wide "backend offline" banner. Renders a prominent, fixed strip at the
 * top of the viewport whenever {@link ConnectionStatusService.offline} is true
 * (the SignalR socket has been down past the grace window). It self-hides the
 * moment the connection returns.
 *
 * Already-loaded data stays on screen behind the banner — the warning's job is
 * to make "this might be stale, the backend is unreachable" immediately
 * obvious, not to blank the UI. Styled from the semantic notify-error tokens so
 * it tracks light/dark themes.
 */
@Component({
  selector: 'app-offline-banner',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './offline-banner.component.html',
  styleUrl: './offline-banner.component.scss',
})
export class OfflineBannerComponent {
  readonly conn = inject(ConnectionStatusService);
}
