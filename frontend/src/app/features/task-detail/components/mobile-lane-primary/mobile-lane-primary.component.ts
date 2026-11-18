import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type { TaskInfo } from '../../../../models/task.model';
import type { AcceptanceRefusal } from '../../state/acceptance-refusal.model';
import { taskPhoneLayout } from '../../state/phone-layout';
import { AcceptanceRefusalComponent } from '../acceptance-refusal/acceptance-refusal.component';

export interface MobileLanePrimary {
  primaryId: string | null;
  primaryLabel: string;
  primaryTooltip: string;
  awaitingGit: boolean;
  blockedByIntegration: boolean;
  blockedByDecision?: boolean;
  actingId: string | null;
  /** AGT-3830: why Accept is held or refused; a content refusal replaces Accept. */
  refusal?: AcceptanceRefusal | null;
}

@Component({
  selector: 'app-mobile-lane-primary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, PendingButtonDirective, AcceptanceRefusalComponent],
  templateUrl: './mobile-lane-primary.component.html',
  styleUrl: './mobile-lane-primary.component.scss',
})
export class MobileLanePrimaryComponent {
  readonly primary = input<MobileLanePrimary | null>(null);
  readonly mutationsBlocked = input(false);
  readonly job = input<TaskInfo | null>(null);
  readonly pressed = output<void>();
  /** The toolbar owns the refusal outside the phone layout. */
  readonly phone = taskPhoneLayout();
}
