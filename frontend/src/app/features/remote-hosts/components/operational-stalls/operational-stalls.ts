import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { OrchestratorFeedStore } from '../../../orchestrator';
import { StallInterventionsService } from '../../services/stall-interventions.service';
import { StallInterventionReceiptComponent } from '../stall-intervention-receipt/stall-intervention-receipt';

const RECENT_INTERVENTIONS = 5;
const REFRESH_MS = 30_000;

@Component({
  selector: 'app-operational-stalls',
  standalone: true,
  imports: [StallInterventionReceiptComponent],
  templateUrl: './operational-stalls.html',
  styleUrl: './operational-stalls.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OperationalStallsComponent {
  private readonly feed = inject(OrchestratorFeedStore);
  protected readonly interventions = inject(StallInterventionsService);
  readonly cases = computed(() => this.feed.openStalls().filter(item =>
    item.subject.startsWith('host:') || item.subject.startsWith('container:')));
  /** AGT-3207: receipts of host and container subjects without an open stall (recent first). */
  readonly settled = computed(() => {
    const open = new Set(this.cases().map(item => item.subject));
    return this.interventions.rows()
      .filter(row => !open.has(row.receipt.subject)
        && (row.receipt.subject.startsWith('host:') || row.receipt.subject.startsWith('container:')))
      .slice(0, RECENT_INTERVENTIONS);
  });

  constructor() {
    this.interventions.refresh();
    const timer = setInterval(() => this.interventions.refresh(), REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  subjectLabel(subject: string): string {
    const [kind, host, service] = subject.split(':');
    return kind === 'container' ? `${service} on ${host}` : host;
  }

  timeLabel(value: string): string {
    return new Intl.DateTimeFormat('en-GB', {
      dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC',
    }).format(new Date(value)) + ' UTC';
  }

  undo(id: string): void {
    this.interventions.undo(id);
  }
}
