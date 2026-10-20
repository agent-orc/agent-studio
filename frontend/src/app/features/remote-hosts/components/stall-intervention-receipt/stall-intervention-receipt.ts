import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { StallInterventionRow } from '../../models/stall-intervention.model';

/** AGT-3207: what the AI Operator observed, did, why, what changed and how to undo it. */
@Component({
  selector: 'app-stall-intervention-receipt',
  standalone: true,
  templateUrl: './stall-intervention-receipt.html',
  styleUrl: './stall-intervention-receipt.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class StallInterventionReceiptComponent {
  readonly row = input.required<StallInterventionRow>();
  readonly undo = output<string>();

  statusLabel(status: string): string {
    switch (status) {
      case 'applying': return 'Being applied';
      case 'applied': return 'Applied, follow-up pending';
      case 'verified': return 'Verified by its effect';
      case 'escalated': return 'Handed to a person';
      case 'proposed': return 'Proposal for a person';
      case 'undone': return 'Undone';
      case 'superseded': return 'Superseded, nothing changed';
      case 'observed': return 'Observe mode, nothing changed';
      default: return status;
    }
  }

  timeLabel(value: string): string {
    return new Intl.DateTimeFormat('en-GB', { timeStyle: 'short', timeZone: 'UTC' }).format(new Date(value)) + ' UTC';
  }
}
