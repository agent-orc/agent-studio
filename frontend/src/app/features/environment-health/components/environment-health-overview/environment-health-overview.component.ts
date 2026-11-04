import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { formatDate } from '@angular/common';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { EnvironmentHealthService } from '../../services/environment-health.service';
import {
  HEALTH_STATUS_LABEL, latencyLabel, uptimeLabel, type EnvironmentHealth, type EnvironmentHealthResponse, type HealthStatus,
} from '../../models/environment-health.model';

/** Environment health on the shared status indicator; invalid declarations need an operator step. */
export const HEALTH_STATUS_TONE: Readonly<Record<HealthStatus, StatusTone>> = {
  up: 'success',
  degraded: 'warning',
  invalid: 'warning',
  down: 'danger',
  unknown: 'neutral',
};

export function healthStatusTone(status: string): StatusTone {
  return HEALTH_STATUS_TONE[status as HealthStatus] ?? 'neutral';
}

/** The current incident cell: an open incident or a watch error is acute, otherwise nothing to report. */
export function healthIncidentDisplay(environment: Pick<EnvironmentHealth, 'currentIncident' | 'error'>): { tone: StatusTone; label: string; detail: string | null } {
  const incident = environment.currentIncident;
  if (incident) {
    const since = formatDate(incident.startedAt, 'MMM d, HH:mm', 'en-US', 'UTC');
    return { tone: 'danger', label: `since ${since} UTC · ${incident.checkId}`, detail: incident.firstError || null };
  }
  if (environment.error) return { tone: 'danger', label: environment.error, detail: environment.error };
  return { tone: 'neutral', label: 'None', detail: null };
}

/** Workspace overview of every declared environment's health (IP-14). */
@Component({
  selector: 'app-environment-health-overview',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './environment-health-overview.component.html',
  styleUrl: './environment-health-overview.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EnvironmentHealthOverviewComponent implements OnInit, OnDestroy {
  private readonly health = inject(EnvironmentHealthService);
  readonly snapshot = signal<EnvironmentHealthResponse | null>(null);
  readonly error = signal<string | null>(null);
  readonly openIncidents = computed(() =>
    this.snapshot()?.environments.filter(item => item.currentIncident !== null).length ?? 0);
  readonly statusLabel = HEALTH_STATUS_LABEL;
  readonly uptime = uptimeLabel;
  readonly latency = latencyLabel;
  readonly statusTone = healthStatusTone;
  readonly incidentDisplay = healthIncidentDisplay;
  private timer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refresh();
    this.timer = setInterval(() => {
      if (!document.hidden) this.refresh();
    }, 30_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  refresh(): void {
    this.health.workspace().subscribe({
      next: value => { this.snapshot.set(value); this.error.set(null); },
      error: () => this.error.set('Environment health is temporarily unavailable.'),
    });
  }
}
