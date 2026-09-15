import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import {
  releaseDriftLabel,
  releaseDriftTone,
  releaseDriftTooltip,
  type StableReleaseIdentity,
} from '../../models/host-release-drift';
import type { RemoteHost } from '../../models/remote-host.model';

/** Compact release identity and Stable-drift state shared by machine and role rows. */
@Component({
  selector: 'app-host-release-identity',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './host-release-identity.html',
  styleUrl: './host-release-identity.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostReleaseIdentityComponent {
  readonly host = input.required<RemoteHost>();
  readonly stableRelease = input<StableReleaseIdentity | null>(null);
  readonly testIdPrefix = input.required<string>();

  readonly releaseLabel = computed(() =>
    this.host().release?.version?.trim() || this.host().releaseId?.trim() || null);
  readonly driftLabel = computed(() => releaseDriftLabel(this.host().releaseDrift));
  readonly driftTone = computed(() => releaseDriftTone(this.host().releaseDrift));
  readonly tooltip = computed(() =>
    releaseDriftTooltip(this.host().releaseDrift, this.stableRelease())
    ?? this.host().releaseId?.trim()
    ?? null);
  readonly tooltipTestId = computed(() => `${this.testIdPrefix()}-tooltip`);
  readonly driftTestId = computed(() => `${this.testIdPrefix()}-drift`);
}
