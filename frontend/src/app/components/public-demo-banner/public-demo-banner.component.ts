import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { PublicDemoModeService } from '../../services/public-demo-mode.service';
import { ShellAccessService } from '../../services/shell-access.service';

/**
 * App-wide "read-only public demo" banner (W34 §8 S4). Renders in shell flow
 * above the titlebar whenever the backend reports the
 * public-demo-readonly execution profile. The banner is explanatory UX only -
 * the server edge (PublicDemoEdgeMiddleware) is what actually denies every
 * mutation; this and publicDemoGuardInterceptor just make that boundary
 * visible before a visitor tries an action that would fail anyway.
 */
@Component({
  selector: 'app-public-demo-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './public-demo-banner.component.html',
  styleUrl: './public-demo-banner.component.scss',
})
export class PublicDemoBannerComponent {
  readonly mode = inject(PublicDemoModeService);

  constructor() {
    if (!inject(ShellAccessService).limited()) this.mode.loadFlags();
  }
}
