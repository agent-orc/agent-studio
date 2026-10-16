import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
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
  imports: [AppTooltipDirective, CopyableTaskKeyComponent],
  templateUrl: './host-release-identity.html',
  styleUrl: './host-release-identity.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostReleaseIdentityComponent {
  readonly host = input.required<RemoteHost>();
  readonly stableRelease = input<StableReleaseIdentity | null>(null);
  readonly showIdentity = input(false);
  readonly now = input(Date.now());
  readonly testIdPrefix = input.required<string>();

  readonly releaseLabel = computed(() =>
    this.host().release?.version?.trim() || this.host().release?.releaseId?.trim() || this.host().releaseId?.trim() || null);
  readonly releaseId = computed(() => this.host().release?.releaseId?.trim() || null);
  readonly commit = computed(() => {
    const reported = this.host().release?.commit?.trim();
    if (reported && /^[0-9a-f]{7,40}$/i.test(reported)) return reported;
    const releaseId = this.releaseId();
    return releaseId?.match(/(?:^|-)([0-9a-f]{7,40})$/i)?.[1] ?? null;
  });
  readonly driftLabel = computed(() => releaseDriftLabel(this.host().releaseDrift));
  readonly driftTone = computed(() => releaseDriftTone(this.host().releaseDrift));
  readonly tooltip = computed(() =>
    releaseDriftTooltip(this.host().releaseDrift, this.stableRelease())
    ?? this.host().releaseId?.trim()
    ?? null);
  readonly tooltipTestId = computed(() => `${this.testIdPrefix()}-tooltip`);
  readonly driftTestId = computed(() => `${this.testIdPrefix()}-drift`);
  readonly reportAge = computed(() => {
    const observed = this.host().releaseDrift?.lastSeenAt ?? this.host().lastHeartbeatAt;
    const age = observed ? this.now() - Date.parse(observed) : Number.NaN;
    if (!Number.isFinite(age) || age <= 90_000) return null;
    return age < 3_600_000 ? `${Math.floor(age / 60_000)}m`
      : age < 86_400_000 ? `${Math.floor(age / 3_600_000)}h`
        : `${Math.floor(age / 86_400_000)}d`;
  });
}
