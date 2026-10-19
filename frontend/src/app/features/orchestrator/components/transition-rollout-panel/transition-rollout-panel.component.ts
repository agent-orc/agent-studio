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
  inputTokens?: number;
  outputTokens?: number;
  unknownPriceCalls?: number;
  evidenceRef: string;
  endedAt: string;
}

/** AGT-3218: what the effect probe found for one applied engine decision. */
interface EffectCheck {
  occurrence: string;
  action: string;
  verified: boolean;
  missing: string[];
  evidence: string;
  checkedAt: string;
}

interface CanaryStatus {
  status: 'watching' | 'passed' | 'failed' | 'stopped';
  required: number;
  verified: number;
  instanceId: string;
  passedOnThisInstance: boolean;
}

interface ApplierContract {
  status: 'covered' | 'uncovered';
  actions: string[];
  uncovered: string[];
}

interface Profile {
  profile: string;
  state: 'monolith' | 'shadow' | 'ready' | 'canary' | 'engine' | 'rolled-back';
  streak: number;
  threshold: number;
  mismatchCount: number;
  lastMismatch: Mismatch | null;
  applierContract?: ApplierContract | null;
  canary?: CanaryStatus | null;
  lastEffectCheck?: EffectCheck | null;
}

interface SpendSummary {
  occurrences: number;
  inputTokens: number;
  outputTokens: number;
  pricedUsd: number;
  unknownPriceCalls: number;
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
  readonly workspaceSpend = signal<SpendSummary | null>(null);
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
      this.workspaceSpend.set(await firstValueFrom(this.http.get<SpendSummary>(
        '/api/orchestration/decision-receipts/spend')).catch(() => null));
      this.error.set(null);
    } catch {
      this.error.set('Could not load transition profiles.');
    }
  }

  price(decision: ShadowDecision): string {
    if (decision.estimatedUsd !== null) {
      const amount = decision.estimatedUsd === 0 && decision.totalTokens > 0
        ? '<$0.0001' : `$${decision.estimatedUsd.toFixed(4)}`;
      return `${amount} priced${decision.unknownPriceCalls ? ` + ${decision.unknownPriceCalls} unknown price` : ''}`;
    }
    return decision.priceStatus === 'no-usage' ? 'no usage data' : 'no price data';
  }

  workspacePrice(spend: SpendSummary): string {
    if (spend.inputTokens + spend.outputTokens === 0) return 'no usage data';
    if (spend.pricedUsd === 0 && spend.unknownPriceCalls > 0) return 'no priced subtotal';
    return spend.pricedUsd === 0 ? '<$0.0001 priced' : `$${spend.pricedUsd.toFixed(4)} priced`;
  }

  canaryText(canary: CanaryStatus): string {
    const here = canary.passedOnThisInstance ? ' · passed on this instance' : '';
    return `${canary.status} · ${canary.verified} / ${canary.required} decisions applied${here}`;
  }

  effectText(check: EffectCheck): string {
    const result = check.verified ? 'applied' : `missing ${check.missing.join(', ')}`;
    return `${check.action} · ${result} · ${check.checkedAt.slice(0, 16).replace('T', ' ')} UTC`;
  }

  async switch(profile: Profile, target: 'shadow' | 'canary' | 'engine' | 'rollback'): Promise<void> {
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
