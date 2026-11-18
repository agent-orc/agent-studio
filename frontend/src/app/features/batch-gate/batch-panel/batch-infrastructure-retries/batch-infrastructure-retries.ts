import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { InfrastructureRetryRequest } from '../../../../models/batch-gate.model';

@Component({
  selector: 'app-batch-infrastructure-retries',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  templateUrl: './batch-infrastructure-retries.html',
})
export class BatchInfrastructureRetriesComponent {
  readonly requests = input<InfrastructureRetryRequest[]>([]);
}
