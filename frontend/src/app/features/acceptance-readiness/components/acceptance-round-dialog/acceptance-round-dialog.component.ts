import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { PendingButtonDirective } from '../../../../components/async-feedback/pending-button.directive';
import { AcceptanceReadinessStore, type AcceptanceRoundDraft } from '../../state/acceptance-readiness.store';

/**
 * AGT-3564 - the owner reads and edits the prefilled round before it goes
 * through the normal continue route. Nothing is sent without "Send round".
 */
@Component({
  selector: 'app-acceptance-round-dialog',
  standalone: true,
  imports: [DialogComponent, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './acceptance-round-dialog.html',
  styleUrl: './acceptance-round-dialog.scss',
})
export class AcceptanceRoundDialogComponent {
  readonly store = inject(AcceptanceReadinessStore);
  readonly draft = input.required<AcceptanceRoundDraft>();
  readonly prompt = signal('');
  readonly baseRef = signal('');

  constructor() {
    effect(() => {
      const draft = this.draft();
      untracked(() => {
        this.prompt.set(draft.item.round?.prompt ?? '');
        this.baseRef.set(draft.item.round?.baseRef ?? '');
      });
    });
  }

  close(): void {
    this.store.closeRound();
  }

  send(): void {
    void this.store.sendRound(this.prompt(), this.baseRef());
  }
}
