import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import type { FeedBurst } from '../orchestrator-feed/feed-bursts';

/**
 * AGT-3585: one stream row for a burst of entries that share a cause, e.g.
 * "15 cards · AI Operator escalated auto-review: ...". Opening it lists the
 * members below and shows the group, with its bulk actions, in the detail pane.
 */
@Component({
  selector: 'app-orchestrator-feed-burst',
  standalone: true,
  imports: [DisclosureMarkerComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed-burst.html',
  styleUrl: './orchestrator-feed-burst.scss',
})
export class OrchestratorFeedBurstComponent {
  private readonly projectLookup = inject(ProjectLookupService);

  readonly burst = input.required<FeedBurst>();
  readonly expanded = input(false);
  readonly selected = input(false);
  readonly toggleRequest = output<FeedBurst>();

  kindLabel(kind: string): string {
    return kind.charAt(0).toUpperCase() + kind.slice(1);
  }

  formatClock(iso: string): string {
    const date = new Date(iso);
    return Number.isNaN(date.getTime())
      ? iso
      : date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
  }

  /** The row count is the member count, so expanding shows exactly this many rows (R3). */
  countLabel(burst: FeedBurst): string {
    const events = burst.entries.length;
    return events === burst.cards ? `${events} cards` : `${events} events · ${burst.cards} cards`;
  }

  span(burst: FeedBurst): string {
    const from = this.formatClock(burst.oldestTs);
    const to = this.formatClock(burst.newestTs);
    return from === to ? `at ${to}` : `${from} to ${to}`;
  }

  projectColor(project: string): string {
    return this.projectLookup.getProjectDisplay(project).color;
  }
}
