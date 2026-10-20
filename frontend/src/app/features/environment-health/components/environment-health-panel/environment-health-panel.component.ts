import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { EnvironmentHealthService } from '../../services/environment-health.service';
import {
  HEALTH_STATUS_LABEL, certificateDaysLeft, latencyLabel, uptimeLabel,
  type EnvironmentHealthCheck, type EnvironmentHealthResponse,
} from '../../models/environment-health.model';

/** Health, uptime, latency and incidents of one project's environments (IP-14). */
@Component({
  selector: 'app-environment-health-panel',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './environment-health-panel.component.html',
  styleUrl: './environment-health-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHealthPanelComponent {
  private readonly health = inject(EnvironmentHealthService);

  readonly projectName = input.required<string>();
  readonly snapshot = signal<EnvironmentHealthResponse | null>(null);
  readonly error = signal<string | null>(null);
  readonly statusLabel = HEALTH_STATUS_LABEL;
  readonly uptime = uptimeLabel;
  readonly latency = latencyLabel;

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  target(check: EnvironmentHealthCheck): string {
    const expectation = check.kind === 'http'
      ? ` · expects ${check.expectedStatus}${check.hasBodyMarker ? ' and body marker' : ''}` : '';
    return `${check.kind.toUpperCase()} ${check.target}${expectation}`;
  }

  rule(check: EnvironmentHealthCheck): string {
    return `Every ${check.intervalSeconds} s · timeout ${check.timeoutSeconds} s · incident after ${check.failureThreshold} failures`
      + `${check.derived ? ' · derived from the environment address' : ''}`;
  }

  lastResult(check: EnvironmentHealthCheck): string {
    const sample = check.lastSample;
    if (!sample) return 'Not probed yet';
    if (sample.ok) return sample.status ? `OK · ${sample.status}` : 'OK';
    return [sample.error ?? 'failed', sample.status].filter(item => item !== null).join(' · ');
  }

  certificate(check: EnvironmentHealthCheck): string | null {
    const days = certificateDaysLeft(check.certificateNotAfter, Date.parse(this.snapshot()?.capturedAt ?? ''));
    return days === null || Number.isNaN(days) ? null : `Certificate expires in ${days} day${days === 1 ? '' : 's'}`;
  }

  certificateWarning(check: EnvironmentHealthCheck): boolean {
    const days = certificateDaysLeft(check.certificateNotAfter, Date.parse(this.snapshot()?.capturedAt ?? ''));
    return days !== null && days <= 14;
  }

  private load(projectName: string): void {
    this.health.project(projectName).subscribe({
      next: value => { this.snapshot.set(value); this.error.set(null); },
      error: () => this.error.set('Environment health could not be loaded.'),
    });
  }
}
