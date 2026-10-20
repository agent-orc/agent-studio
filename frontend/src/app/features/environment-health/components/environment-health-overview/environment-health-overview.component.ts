import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { EnvironmentHealthService } from '../../services/environment-health.service';
import {
  HEALTH_STATUS_LABEL, latencyLabel, uptimeLabel, type EnvironmentHealthResponse,
} from '../../models/environment-health.model';

/** Workspace overview of every declared environment's health (IP-14). */
@Component({
  selector: 'app-environment-health-overview',
  standalone: true,
  imports: [DatePipe],
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
