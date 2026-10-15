import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

interface DecisionReceipt {
  occurrence: string;
  inputsDigest: string;
  action: string;
  reason: string;
}

interface Mismatch {
  monolith: DecisionReceipt;
  engine: DecisionReceipt;
}

/** AGT-3063 shadow model decision receipt row; evidenceRef opens the full receipt. */
interface ShadowDecision {
  receiptId: string;
  taskId: string | null;
  profile: string;
  chosenAction: string;
  deterministicAction: string;
  matchesDeterministic: boolean;
  callStatus: string;
  model: string | null;
  thinkingLevel: string | null;
  custody: string;
  totalTokens: number;
  priceStatus: string;
  estimatedUsd: number | null;
  evidenceRef: string;
  endedAt: string;
}

interface Profile {
  profile: string;
  state: 'monolith' | 'shadow' | 'ready' | 'engine' | 'rolled-back';
  streak: number;
  threshold: number;
  mismatchCount: number;
  lastMismatch: Mismatch | null;
}

@Component({
  selector: 'app-transition-rollout-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transition-rollout-panel.component.html',
  styleUrl: './transition-rollout-panel.component.scss',
})
export class TransitionRolloutPanelComponent implements OnInit {
  private readonly http = inject(HttpClient);
  readonly profiles = signal<readonly Profile[]>([]);
  readonly decisions = signal<readonly ShadowDecision[]>([]);
  readonly reason = signal('');
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void { void this.refresh(); }

  async refresh(): Promise<void> {
    try {
      const decisions = firstValueFrom(
        this.http.get<ShadowDecision[]>('/api/orchestration/decision-receipts?limit=5')).catch(() => []);
      this.profiles.set(await firstValueFrom(
        this.http.get<Profile[]>('/api/orchestration/rollout/profiles')));
      this.decisions.set(await decisions);
      this.error.set(null);
    } catch {
      this.error.set('Could not load transition profiles.');
    }
  }

  price(decision: ShadowDecision): string {
    if (decision.estimatedUsd !== null) return `$${decision.estimatedUsd.toFixed(4)}`;
    return decision.priceStatus === 'no-usage' ? 'no usage data' : 'no price data';
  }

  async switch(profile: Profile, target: 'shadow' | 'engine' | 'rollback'): Promise<void> {
    const reason = this.reason().trim();
    if (!reason || this.busy()) return;
    this.busy.set(profile.profile);
    try {
      await firstValueFrom(this.http.post(
        `/api/orchestration/rollout/profiles/${profile.profile}/switch`, { target, reason }));
      this.reason.set('');
      await this.refresh();
    } catch {
      this.error.set(`Could not switch ${profile.profile}. Check its current state and try again.`);
    } finally {
      this.busy.set(null);
    }
  }
}
