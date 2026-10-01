import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { WikiLifecycleItem, WikiNodeType, WikiPulse, WikiPulseDriftArea, WikiPulseFeedItem, WorkbenchListItem } from '../../../../../models/project-docs.model';
import { WorkbenchInboxComponent } from './workbench-inbox/workbench-inbox.component';
import type { DecisionInboxItem } from '../../../../../models/decision-card-presentation';

/** What the parent needs to open a page from a Pulse row. */
export interface WikiPulseOpenRequest {
  relPath: string;
  type: WikiNodeType;
}

type WikiPulseTone = 'good' | 'info' | 'warn' | 'bad' | 'muted';

/** The compact feed shows this many rows; the rest sits behind "Alle anzeigen". */
const FEED_COMPACT_COUNT = 8;

/**
 * The generated Pulse cards of the wiki landing dashboard (PULSE-1). The host
 * renders `display: contents`, so each card slots directly into the dashboard's
 * grid:
 *
 *  - the full-width drift strip (per top-level docs folder, deterministic),
 *  - "Zuletzt geändert" (compact change feed, expandable behind a UI toggle),
 *  - "Aufmerksamkeit" (warnings + unfiled inbox; the card hides when clear),
 *  - "Dossiers" (catalogue via {@link WorkbenchInboxComponent}),
 *  - "In Arbeit" (docs-touching live runs).
 *
 * Purely presentational: the parent owns fetching and feeds the fully-composed
 * {@link WikiPulse}; this component only formats and emits navigation intent.
 */
@Component({
  selector: 'app-wiki-pulse',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective, WorkbenchInboxComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-pulse.component.html',
  styleUrl: './wiki-pulse.component.scss',
})
export class WikiPulseComponent {
  readonly pulse = input<WikiPulse | null>(null);
  readonly loading = input(false);
  /** AGT-2795: pending decision cards listed beside the Dossier decisions. */
  readonly decisionCards = input<readonly DecisionInboxItem[]>([]);

  readonly openPage = output<WikiPulseOpenRequest>();
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly openDecisionCard = output<DecisionInboxItem>();

  readonly feed = computed(() => this.pulse()?.feed ?? null);
  readonly inbox = computed(() => this.pulse()?.inbox ?? null);
  readonly drift = computed(() => this.pulse()?.drift ?? null);
  readonly warnings = computed(() => this.pulse()?.warnings ?? null);
  readonly activity = computed(() => this.pulse()?.activity ?? null);

  readonly feedItems = computed<readonly WikiPulseFeedItem[]>(() => this.feed()?.items ?? []);
  readonly hasFeedItems = computed(() => this.feedItems().length > 0);

  /** Pure UI state: whether the feed card shows all rows or the compact head. */
  readonly feedExpanded = signal(false);

  readonly visibleFeedItems = computed<readonly WikiPulseFeedItem[]>(() =>
    this.feedExpanded() ? this.feedItems() : this.feedItems().slice(0, FEED_COMPACT_COUNT));

  readonly hiddenFeedCount = computed(() =>
    Math.max(0, this.feedItems().length - FEED_COMPACT_COUNT));

  toggleFeedExpanded(): void {
    this.feedExpanded.update(v => !v);
  }

  /**
   * The "Aufmerksamkeit" card mounts only when something needs a human: at
   * least one warning or unfiled page, or a source that degraded to a reason.
   * A clear inbox with no warnings leaves no card in the grid.
   */
  readonly attentionVisible = computed(() => {
    const warnings = this.warnings();
    const inbox = this.inbox();
    const warningsNeed = !!warnings && (!warnings.available || warnings.count > 0);
    const inboxNeeds = !!inbox && (!inbox.available || inbox.count > 0);
    return warningsNeed || inboxNeeds;
  });

  readonly attentionCount = computed(() =>
    (this.warnings()?.count ?? 0) + (this.inbox()?.count ?? 0));

  openWarning(warning: { relPath: string | null }): void {
    // Repo-level warnings (e.g. a missing docs folder) carry no document path;
    // there is nothing to open then.
    if (!warning.relPath) return;
    this.openPage.emit({ relPath: warning.relPath, type: this.typeForRel(warning.relPath) });
  }

  openFeed(item: WikiPulseFeedItem): void {
    this.openPage.emit({ relPath: item.relPath, type: this.typeForRel(item.relPath) });
  }

  openLifecyclePage(item: WikiLifecycleItem): void {
    this.openPage.emit({ relPath: item.relPath, type: this.typeForRel(item.relPath) });
  }

  /** Tone for a drift grade (Fresh -> good, Aging -> warn, Stale -> bad). */
  gradeTone(grade: string | null | undefined): WikiPulseTone {
    switch ((grade ?? '').toLowerCase()) {
      case 'fresh': return 'good';
      case 'aging': return 'warn';
      case 'stale': return 'bad';
      default: return 'muted';
    }
  }

  /** Short area caption for the compact grade-bar segment. */
  areaCaption(count: number, grade: string): string {
    if (grade === 'Empty') return 'no pages';
    return count === 1 ? '1 commit' : `${count} commits`;
  }

  /** Full drift breakdown for a grade-bar segment's hover tooltip. */
  areaTooltip(area: WikiPulseDriftArea): string {
    return `${area.title} - ${area.grade} · ${area.gradedPageCount} graded / ${area.pageCount} pages · worst ${area.worstCommitCount} commits`;
  }

  typeForRel(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }

  kindLabel(type: WikiNodeType): string {
    return type === 'html' ? 'HTML' : type === 'json' ? 'JSON' : 'MD';
  }

  /** Compact relative time ("3h ago"), falling back to a locale date. */
  relativeTime(iso: string): string {
    const then = new Date(iso);
    const ms = then.getTime();
    if (Number.isNaN(ms)) return iso;
    const diff = Date.now() - ms;
    if (diff < 0) return then.toLocaleDateString('en-US');
    const min = Math.floor(diff / 60000);
    if (min < 1) return 'just now';
    if (min < 60) return `${min}m ago`;
    const hours = Math.floor(min / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 30) return `${days}d ago`;
    return then.toLocaleDateString('en-US');
  }

  absoluteTime(iso: string): string {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString('en-US');
  }

  runtime(iso: string): string {
    const ms = Date.now() - new Date(iso).getTime();
    if (!Number.isFinite(ms) || ms < 0) return 'just started';
    const minutes = Math.max(1, Math.floor(ms / 60_000));
    if (minutes < 60) return `${minutes}m`;
    const hours = Math.floor(minutes / 60);
    return `${hours}h ${minutes % 60}m`;
  }
}
