import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PendingButtonDirective } from '../../../../components/async-feedback';

export interface MobileLanePrimary {
  primaryId: string | null;
  primaryLabel: string;
  primaryTooltip: string;
  awaitingGit: boolean;
  blockedByIntegration: boolean;
  blockedByDecision?: boolean;
  actingId: string | null;
}

@Component({
  selector: 'app-mobile-lane-primary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, PendingButtonDirective],
  templateUrl: './mobile-lane-primary.component.html',
  styleUrl: './mobile-lane-primary.component.scss',
})
export class MobileLanePrimaryComponent {
  readonly primary = input<MobileLanePrimary | null>(null);
  readonly mutationsBlocked = input(false);
  readonly pressed = output<void>();
}
