import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { identityDisplay } from './identity-display';

/**
 * Shared identity chip for people, service accounts and personas (AGT-4034).
 * Shows the name and a kind icon; the full id is only in the tooltip. The chip
 * has a maximum width, shrinks before anything else in its row, and ends in an
 * ellipsis.
 */
@Component({
  selector: 'app-identity-chip',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './identity-chip.component.html',
  styleUrl: './identity-chip.component.scss',
})
export class IdentityChipComponent {
  readonly id = input<string | null | undefined>(null);
  readonly name = input<string | null | undefined>(null);
  /** Name of the token a service account acts through, e.g. "operator session". */
  readonly tokenName = input<string | null | undefined>(null);
  readonly kind = input<string | null | undefined>(null);
  /** Optional prefix for the tooltip, e.g. "Owner". */
  readonly tooltipPrefix = input<string | null>(null);

  readonly view = computed(() => identityDisplay(this.id(), {
    name: this.name(), tokenName: this.tokenName(), kind: this.kind(),
  }));
  readonly tooltip = computed(() => {
    const role = this.tooltipPrefix();
    return role ? `${role}\n${this.view().tooltip}` : this.view().tooltip;
  });
}
