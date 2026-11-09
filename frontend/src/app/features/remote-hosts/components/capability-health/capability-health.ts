import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { taskServerRouteDetail, taskServerRouteStatus } from '../../models/remote-host.model';
import type { RemoteHost, RemoteHostCapabilityHealth } from '../../models/remote-host.model';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-capability-health',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './capability-health.html',
  styleUrl: './capability-health.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CapabilityHealthComponent {
  readonly host = input.required<RemoteHost>();
  readonly routeStatus = computed(() => taskServerRouteStatus(this.host()));
  readonly routeDetail = computed(() => taskServerRouteDetail(this.host()));

  tone(capability: RemoteHostCapabilityHealth): 'ok' | 'warn' | 'error' | 'idle' {
    if (!capability.isFresh) return 'error';
    if (capability.advertisedStatus !== 'ready') {
      return capability.advertisedStatus === 'unavailable' ? 'error' : 'warn';
    }
    switch (capability.healthState) {
      case 'healthy': return 'ok';
      case 'suspect': return 'warn';
      case 'draining': return 'error';
      case 'half-open': return 'idle';
    }
  }

  /** Capability health on the shared indicator; a half-open recovery probe is work in progress. */
  indicatorTone(capability: RemoteHostCapabilityHealth): StatusTone {
    switch (this.tone(capability)) {
      case 'ok': return 'success';
      case 'warn': return 'warning';
      case 'error': return 'danger';
      default: return 'running';
    }
  }

  displayState(capability: RemoteHostCapabilityHealth): string {
    if (!capability.isFresh) return 'stale';
    if (capability.advertisedStatus !== 'ready') return capability.advertisedStatus;
    return capability.healthState;
  }
}
