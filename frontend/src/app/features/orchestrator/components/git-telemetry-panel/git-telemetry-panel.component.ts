import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { GitTelemetryService } from '../../../../services/git-telemetry.service';

/**
 * Admin panel section for the AGT-2726 board-performance SLO: per-endpoint
 * git-spawn p50/p95/rate over the last hour, and per-repository background
 * index freshness. Read-only and polled on demand (a Refresh button), not
 * kept live - this is a diagnostic surface, not a board.
 */
@Component({
  selector: 'app-git-telemetry-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './git-telemetry-panel.component.html',
  styleUrl: './git-telemetry-panel.component.scss',
})
export class GitTelemetryPanelComponent implements OnInit {
  private readonly api = inject(GitTelemetryService);
  readonly snapshot = this.api.snapshot;
  readonly loadError = this.api.loadError;

  ngOnInit(): void {
    void this.api.load();
  }

  refresh(): void {
    void this.api.load();
  }

  ageLabel(seconds: number | null): string {
    if (seconds === null) return 'never indexed';
    if (seconds < 60) return `${Math.round(seconds)}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    return `${Math.round(seconds / 3600)}h ago`;
  }
}
