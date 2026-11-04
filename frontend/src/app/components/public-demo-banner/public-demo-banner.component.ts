import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';
import { PublicDemoModeService } from '../../services/public-demo-mode.service';

/**
 * App-wide "read-only public demo" banner (W34 §8 S4). Renders a fixed strip
 * at the top of the viewport whenever the backend reports the
 * public-demo-readonly execution profile. The banner is explanatory UX only -
 * the server edge (PublicDemoEdgeMiddleware) is what actually denies every
 * mutation; this and publicDemoGuardInterceptor just make that boundary
 * visible before a visitor tries an action that would fail anyway.
 */
@Component({
  selector: 'app-public-demo-banner',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './public-demo-banner.component.html',
  styleUrl: './public-demo-banner.component.scss',
})
export class PublicDemoBannerComponent {
  readonly mode = inject(PublicDemoModeService);

  constructor() {
    this.mode.loadFlags();
  }
}
