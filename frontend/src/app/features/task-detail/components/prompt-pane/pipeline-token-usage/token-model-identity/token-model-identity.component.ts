import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ModelLevelIndicatorComponent } from '../../../../../../components/model-level-indicator/model-level-indicator.component';

/**
 * One (model, reasoning level) identity as the token panel names it.
 *
 * The level is the second half of a model's identity: the same model at `low`
 * and at `high` is neither the same cost nor the same quality, so a token row
 * that names only the model names only half of what produced the numbers
 * (operator, 2026-09-14). This component renders the pair once, so the panel
 * has a single place that decides how a model is spelled out:
 *
 *  - the shared board badge (`app-model-level-indicator`), so the compact
 *    vocabulary (`OP4.8 m`) and its family colour are identical to the card;
 *  - the full catalogue id plus the full level word (`claude-opus-4-8 ·
 *    medium`), because this panel has the room the card does not.
 *
 * A row whose recorded data carries no level says `level unknown` instead of
 * borrowing one: the level is read from the ledger, never guessed.
 */
@Component({
  selector: 'app-token-model-identity',
  standalone: true,
  imports: [ModelLevelIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-testid]': 'testId()', '[attr.data-thinking-level]': 'level()' },
  templateUrl: './token-model-identity.component.html',
  styleUrl: './token-model-identity.component.scss',
})
export class TokenModelIdentityComponent {
  readonly model = input<string>('');
  readonly thinkingLevel = input<string | null | undefined>(null);
  /** Calls / steps folded into this identity; the ×N chip stays hidden at 1. */
  readonly steps = input<number>(1);
  readonly testId = input('token-model-identity');

  /** Trimmed level, or null when the recorded data has none. */
  readonly level = computed<string | null>(() => {
    const raw = this.thinkingLevel()?.trim();
    return raw ? raw : null;
  });

  readonly levelLabel = computed<string>(() => this.level() ?? 'level unknown');
}
