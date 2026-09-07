import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { ModelMigrationProposal } from '../../models/model-migration.model';

/** Shared proposal presentation used on cards, pipeline settings, and CLI Management. */
@Component({
  selector: 'app-model-migration-offer',
  standalone: true,
  templateUrl: './model-migration-offer.html',
  styleUrl: './model-migration-offer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.model-migration-host--compact]': 'compact()',
  },
})
export class ModelMigrationOfferComponent {
  readonly proposal = input.required<ModelMigrationProposal>();
  readonly compact = input(false);
  readonly busy = input(false);
  readonly apply = output<ModelMigrationProposal>();

  costClass(value: string | null | undefined): string {
    return value?.trim() || 'not classified';
  }

  reasoningLadder(value: string[] | null | undefined): string {
    return value?.filter(Boolean).join(' / ') || 'not specified';
  }

  applyUpdate(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.busy()) this.apply.emit(this.proposal());
  }
}
