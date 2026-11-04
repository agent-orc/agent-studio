import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { ServerReadinessService } from '../../services/server-readiness.service';
import {
  READINESS_STATE_LABEL, READINESS_STATE_TONE, groupReadiness, readinessKey, type ReadinessState, type ServerReadinessReport,
} from '../../models/server-readiness.model';

/**
 * The readiness report the server computes about itself (AGT-3412): one line
 * per check with its state, what it means, and the exact remedy.
 */
@Component({
  selector: 'app-server-readiness-panel',
  standalone: true,
  imports: [DisclosureMarkerComponent, StatusIndicatorComponent],
  templateUrl: './server-readiness-panel.component.html',
  styleUrl: './server-readiness-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ServerReadinessPanelComponent implements OnInit, OnDestroy {
  private readonly readiness = inject(ServerReadinessService);
  readonly report = signal<ServerReadinessReport | null>(null);
  readonly error = signal<string | null>(null);
  readonly checking = signal(false);
  /** Problems first: passing lines are one click away, so a ready server stays short. */
  readonly showPassing = signal(false);
  readonly passing = computed(() => this.report()?.checks.filter(check => check.state === 'pass').length ?? 0);
  readonly groups = computed(() => groupReadiness((this.report()?.checks ?? [])
    .filter(check => this.showPassing() || check.state !== 'pass')));
  readonly summary = computed(() => {
    const report = this.report();
    if (!report?.computedAt) return '';
    const at = new Date(report.computedAt).toLocaleTimeString('en-US', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    const counts = report.failing || report.attention
      ? [report.failing ? `${report.failing} failing` : null, report.attention ? `${report.attention} need attention` : null]
        .filter(Boolean).join(' · ')
      : `All ${report.checks.length} checks pass`;
    return `${counts} · checked ${at} UTC`;
  });
  readonly label = READINESS_STATE_LABEL;

  /** A state the server adds later stays neutral until it is mapped. */
  stateTone(state: ReadinessState): StatusTone {
    return READINESS_STATE_TONE[state] ?? 'neutral';
  }
  readonly key = readinessKey;
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => {
      if (!document.hidden) this.refresh();
    }, 60_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  refresh(): void {
    this.readiness.read().subscribe({
      next: value => { this.report.set(value); this.error.set(null); },
      error: () => this.error.set('Server readiness is temporarily unavailable.'),
    });
  }

  recheck(): void {
    if (this.checking()) return;
    this.checking.set(true);
    this.readiness.recheck().subscribe({
      next: value => { this.report.set(value); this.error.set(null); this.checking.set(false); },
      error: () => { this.error.set('The recheck did not finish. Try again in a minute.'); this.checking.set(false); },
    });
  }
}
