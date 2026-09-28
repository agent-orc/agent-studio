import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject } from '@angular/core';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';

import { TooltipDirective } from 'coding-agent-chat/shared';
@Component({
  selector: 'app-auto-review-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './auto-review-indicator.html',
  styleUrl: './auto-review-indicator.scss'
})
export class AutoReviewIndicatorComponent implements OnInit, OnDestroy {
  private readonly statusStore = inject(AutoReviewStatusStore);
  private readonly staleMs = 90_000;

  readonly status = this.statusStore.status;

  readonly tone = computed<'active' | 'idle' | 'stale' | 'alert'>(() => {
    const s = this.status();
    if (!s?.lastTickAt) return 'stale';
    if (s.escalationRateAlert) return 'alert';
    if (s.currentJob) return 'active';
    const age = Date.now() - Date.parse(s.lastTickAt);
    return age > this.staleMs ? 'stale' : 'idle';
  });

  readonly label = computed(() => {
    const s = this.status();
    if (!s?.lastTickAt) return 'Auto-review starting';
    if (s.escalationRateAlert) return `Escalation ${(s.escalationRate ?? 0).toLocaleString('en-US', { style: 'percent', maximumFractionDigits: 0 })}`;
    if (s.currentJob) return 'Auto-review running';
    const pending = s.pending ?? 0;
    if (pending > 0) return `Auto-review ${pending} queued`;
    return 'Auto-review idle';
  });

  readonly tooltip = computed(() => {
    const s = this.status();
    if (!s) return 'Auto-review status has not loaded yet.';
    if (!s.lastTickAt) return 'Auto-review has not completed its first tick since backend start.';
    const tick = new Date(s.lastTickAt).toLocaleString('en-US');
    const current = s.currentJob ? `\nCurrent: ${s.currentProject ?? 'unknown project'} / ${s.currentJob}` : '';
    const rate = (s.escalationRate ?? 0).toLocaleString('en-US', { style: 'percent', maximumFractionDigits: 0 });
    const threshold = (s.escalationRateAlertThreshold ?? 0).toLocaleString('en-US', { style: 'percent', maximumFractionDigits: 0 });
    const alert = s.escalationRateAlert
      ? `\nEscalation-rate alert: ${rate} is above ${threshold}.`
      : '';
    return `Auto-review last tick: ${tick}\n` +
      `Candidates: ${s.pending ?? 0}\n` +
      `Accept: ${s.accept}, reissue: ${s.reissue}, escalate: ${s.escalate}, aspects: ${s.aspectsRun}\n` +
      `Escalation rate: ${rate} (${s.escalationRateDecisionCount ?? 0} accept/escalate decisions)` +
      alert +
      current;
  });

  ngOnInit(): void {
    this.statusStore.subscribe();
  }

  ngOnDestroy(): void {
    this.statusStore.release();
  }
}
