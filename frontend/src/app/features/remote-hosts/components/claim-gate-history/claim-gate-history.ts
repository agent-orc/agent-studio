import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import {
  claimGateEventDetail,
  claimGateEventTitle,
  type ClaimGateEvent,
} from '../../models/claim-load-gate';
import type { RemoteHost } from '../../models/remote-host.model';

/**
 * Execution Hosts History: when a host stopped and resumed claiming new work,
 * with the load and duration of each transition. History renders quietly (R4);
 * the live gate state is on the host card and in the attention list.
 */
@Component({
  selector: 'app-claim-gate-history',
  standalone: true,
  imports: [DatePipe, StatusIndicatorComponent],
  templateUrl: './claim-gate-history.html',
  styleUrl: './claim-gate-history.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ClaimGateHistoryComponent {
  readonly events = input.required<readonly ClaimGateEvent[]>();
  readonly hosts = input<readonly RemoteHost[]>([]);
  readonly title = claimGateEventTitle;
  readonly detail = claimGateEventDetail;

  private readonly hostNames = computed(() => new Map(this.hosts()
    .flatMap(host => [[host.clientId, host.name], [host.id, host.name]] as const)));

  hostName(event: ClaimGateEvent): string {
    return this.hostNames().get(event.runnerId) ?? event.runnerId;
  }
}
