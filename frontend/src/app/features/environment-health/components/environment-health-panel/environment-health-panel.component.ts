import { ChangeDetectionStrategy, Component, effect, inject, input, signal, type TemplateRef } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { StatusIndicatorComponent, type StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';
import { EnvironmentHealthService } from '../../services/environment-health.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import {
  HEALTH_STATUS_LABEL, certificateDaysLeft, latencyLabel, uptimeLabel,
  type EnvironmentHealthCheck, type EnvironmentHealthResponse, type HealthStatus,
} from '../../models/environment-health.model';

/** Health status of an environment or check mapped onto the shared tones (AGT-3403). */
const HEALTH_STATUS_TONE: Readonly<Record<HealthStatus, StatusTone>> = {
  up: 'success',
  degraded: 'warning',
  invalid: 'warning',
  down: 'danger',
  unknown: 'neutral',
  'not-observed': 'neutral',
  'not-deployed': 'neutral',
};

export function healthStatusTone(status: string): StatusTone {
  return Object.hasOwn(HEALTH_STATUS_TONE, status) ? HEALTH_STATUS_TONE[status as HealthStatus] : 'neutral';
}

/** Health, uptime, latency and incidents of one project's environments (IP-14). */
@Component({
  selector: 'app-environment-health-panel',
  standalone: true,
  imports: [DatePipe, NgTemplateOutlet, StatusIndicatorComponent],
  templateUrl: './environment-health-panel.component.html',
  styleUrl: './environment-health-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHealthPanelComponent {
  private readonly health = inject(EnvironmentHealthService);
  private readonly references = inject(TaskReferenceNavigationService);

  readonly projectName = input.required<string>();
  /**
   * What the host offers on an open incident, such as the Deploy rollback
   * (F13). The context is the environment id; the panel itself stays
   * independent of Deploy.
   */
  readonly incidentAction = input<TemplateRef<{ $implicit: string }> | null>(null);
  readonly snapshot = signal<EnvironmentHealthResponse | null>(null);
  readonly error = signal<string | null>(null);
  readonly statusLabel = HEALTH_STATUS_LABEL;
  readonly uptime = uptimeLabel;
  readonly latency = latencyLabel;
  readonly statusTone = healthStatusTone;

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  /** The incident card by the key the board shows (NS-2). */
  openCard(card: string): void {
    this.references.openReferenceOrNotify(card);
  }

  target(check: EnvironmentHealthCheck): string {
    if (check.kind === 'telemetry') return `Telemetry · ${check.target}`;
    const expectation = check.kind === 'http'
      ? ` · expects ${check.expectedStatus}${check.hasBodyMarker ? ' and body marker' : ''}` : '';
    return `${check.kind.toUpperCase()} ${check.target}${expectation}`;
  }

  rule(check: EnvironmentHealthCheck): string {
    if (check.kind === 'telemetry') return 'Evaluated from environment telemetry';
    return `Every ${check.intervalSeconds} s · timeout ${check.timeoutSeconds} s · incident after ${check.failureThreshold} failures`
      + `${check.derived ? ' · derived from the environment address' : ''}`;
  }

  lastResult(check: EnvironmentHealthCheck): string {
    const sample = check.lastSample;
    if (!sample) return 'Not probed yet';
    if (sample.error === 'not-observed') return 'Not observed';
    if (check.kind === 'telemetry') return sample.error ?? 'Observed';
    if (sample.ok) return sample.status ? `OK · ${sample.status}` : 'OK';
    return [sample.error ?? 'failed', sample.status].filter(item => item !== null).join(' · ');
  }

  lastResultTone(check: EnvironmentHealthCheck): StatusTone {
    const sample = check.lastSample;
    if (!sample) return 'neutral';
    if (sample.error === 'not-observed') return 'neutral';
    return sample.ok ? 'success' : 'danger';
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
