import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { splitReadyWaits, type ReadyWaitBucket } from '../../../../models/ready-wait-reason';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { effectivePickupHold } from '../../../../components/pickup-hold/effective-pickup-hold';
import { RemoteHostsService } from '../../../remote-hosts';

const LISTED_KEYS = 5;

/**
 * AGT-3579 - the Ready lane header line that says why its cards wait.
 *
 * Every card counts once, under a card hold or under the reason the runner
 * recorded for it, so the buckets add up to the lane count (R3). The line
 * renders only when at least one card waits for a named reason; a lane of
 * plainly queued cards has nothing to explain.
 */
@Component({
  selector: 'app-ready-wait-split',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ready-wait-split.component.html',
  styleUrl: './ready-wait-split.component.scss',
})
export class ReadyWaitSplitComponent {
  private readonly remoteHosts = inject(RemoteHostsService, { optional: true });
  readonly jobs = input.required<readonly TaskInfo[]>();

  readonly buckets = computed<ReadyWaitBucket[]>(() => {
    const hosts = this.remoteHosts?.hosts() ?? [];
    return splitReadyWaits(this.jobs(), task => effectivePickupHold(task, hosts));
  });

  readonly total = computed(() => this.buckets().reduce((sum, bucket) => sum + bucket.count, 0));
  readonly visible = computed(() => this.buckets().some(bucket => bucket.kind !== 'queued'));

  readonly summary = computed(() => this.buckets()
    .map(bucket => `${bucket.count} ${bucket.label}`)
    .join(', '));

  tooltip(bucket: ReadyWaitBucket): string {
    const listed = bucket.keys.slice(0, LISTED_KEYS).join(', ');
    const more = bucket.keys.length > LISTED_KEYS ? ` and ${bucket.keys.length - LISTED_KEYS} more` : '';
    const source = bucket.kind === 'hold' ? 'Card hold' : bucket.kind === 'runner' ? 'Runner reason' : 'No reason recorded';
    return `${source}: ${bucket.label}\n${listed}${more}`;
  }
}
