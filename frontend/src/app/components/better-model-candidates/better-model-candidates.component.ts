import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BetterModelCandidate } from '../../models/task.model';

/** Compact, read-only projection of TokenEconomy route candidates. */
@Component({
  selector: 'app-better-model-candidates',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './better-model-candidates.component.html',
  styleUrl: './better-model-candidates.component.scss',
})
export class BetterModelCandidatesComponent {
  readonly candidates = input.required<readonly BetterModelCandidate[]>();
  readonly testidPrefix = input.required<string>();
}
