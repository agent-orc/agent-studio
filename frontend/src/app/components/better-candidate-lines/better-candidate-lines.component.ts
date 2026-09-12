import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BetterCandidate, BetterCandidateNote } from '../../models/task.model';

@Component({
  selector: 'app-better-candidate-lines',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './better-candidate-lines.component.html',
  styleUrl: './better-candidate-lines.component.scss',
})
export class BetterCandidateLinesComponent {
  readonly note = input.required<BetterCandidateNote>();
  readonly prefix = input<string>('Better benchmark candidate');

  candidateKey(candidate: BetterCandidate): string {
    return `${candidate.model}|${candidate.thinkingLevel ?? ''}|${candidate.benchmarkType}`;
  }

  route(candidate: BetterCandidate): string {
    return `${candidate.model}/${candidate.thinkingLevel ?? 'default'}`;
  }

  score(value: number | null): string {
    if (value === null) return 'SΔ n/a';
    return `SΔ${value > 0 ? '+' : ''}${value.toFixed(2).replace(/\.00$/, '')}`;
  }

  cost(value: number | null): string {
    if (value === null) return '$Δ n/a';
    const sign = value > 0 ? '+' : value < 0 ? '-' : '';
    return `$Δ${sign}${Math.abs(value).toFixed(4).replace(/0+$/, '').replace(/\.$/, '')}`;
  }

  description(candidate: BetterCandidate): string {
    return `${this.route(candidate)}, ${candidate.benchmarkName} (${candidate.benchmarkType}), `
      + `score delta ${candidate.scoreDelta ?? 'n/a'}, cost delta ${candidate.costDeltaUsd ?? 'n/a'} USD, `
      + `evidence ${candidate.evidenceAgeDays} days old`;
  }
}
