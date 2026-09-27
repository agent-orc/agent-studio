import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import type { CliModelInfo } from '../../../../cli';
import {
  WikiGradingRunStatus,
  WikiPulse,
  WorkbenchListItem,
} from '../../../../../models/project-docs.model';
import { ProjectStyleGuidesPanelComponent } from '../../project-style-guides-panel/project-style-guides-panel';
import { WikiGradePanelComponent } from '../wiki-grade-panel/wiki-grade-panel.component';
import { WikiHomeLinksComponent } from '../wiki-home-links/wiki-home-links.component';
import { WikiPulseComponent, WikiPulseOpenRequest } from '../wiki-pulse/wiki-pulse.component';
import { WikiStarredPanelComponent } from '../wiki-starred-panel/wiki-starred-panel.component';

type WikiDashboardTone = 'good' | 'warn' | 'bad' | 'muted';

/**
 * Wiki landing dashboard: the entry surface the wiki opens on when no page is
 * selected. A head row (title, quick actions, stat tiles fed by the Pulse
 * payload) sits above a responsive card grid composed from the landing card
 * components - starred pages, curated entry sections, the Pulse cards (drift,
 * recent changes, attention, workbenches, activity), style guides, and the
 * grading trigger.
 *
 * Purely presentational: the parent owns every fetch and mutation; this
 * component only lays the cards out and forwards their intent outputs.
 */
@Component({
  selector: 'app-wiki-dashboard',
  standalone: true,
  imports: [
    ProjectStyleGuidesPanelComponent,
    StudioIconComponent,
    WikiGradePanelComponent,
    WikiHomeLinksComponent,
    WikiPulseComponent,
    WikiStarredPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-dashboard.component.html',
  styleUrl: './wiki-dashboard.component.scss',
})
export class WikiDashboardComponent {
  readonly projectName = input.required<string>();
  readonly docCount = input(0);
  readonly pulse = input<WikiPulse | null>(null);
  readonly loading = input(false);
  readonly hasStarred = input(false);

  // Grading trigger pass-through (state lives in the parent, AGT-2051).
  readonly gradingStatus = input<WikiGradingRunStatus | null>(null);
  readonly gradeModel = input<string | null>(null);
  readonly gradeLevel = input<string | null>(null);
  readonly gradeModels = input<readonly CliModelInfo[]>([]);

  /** Open a wiki page in the reader (starred entries, home links, Pulse rows). */
  readonly openPage = output<WikiPulseOpenRequest>();
  readonly openWorkbench = output<WorkbenchListItem>();
  readonly openFirst = output<void>();
  readonly openDrift = output<void>();
  readonly startGrading = output<void>();
  readonly abortGrading = output<void>();
  readonly gradeModelChange = output<string>();
  readonly gradeLevelChange = output<string | null>();
  /** Emits a page relPath to open straight to its companion report tab. */
  readonly openReport = output<string>();
  /** Emits a style-guide relPath to open in the reader. */
  readonly openGuide = output<string>();

  readonly driftCounts = computed(() => this.pulse()?.drift?.counts ?? null);

  /** Overall drift verdict for the head status chip (small icon + label only). */
  readonly overallDrift = computed<{ grade: string; tone: WikiDashboardTone } | null>(() => {
    const drift = this.pulse()?.drift;
    if (!drift?.available) return null;
    const grade = drift.overallGrade;
    const tone: WikiDashboardTone = grade === 'Fresh' ? 'good'
      : grade === 'Aging' ? 'warn'
        : grade === 'Stale' ? 'bad'
          : 'muted';
    return { grade, tone };
  });

  /** "vor X" caption of the newest feed entry (the feed arrives newest first). */
  readonly lastEditedLabel = computed(() => {
    const iso = this.pulse()?.feed?.items?.[0]?.authorDateUtc;
    if (!iso) return '–';
    const ms = new Date(iso).getTime();
    if (Number.isNaN(ms)) return '–';
    const minutes = Math.floor((Date.now() - ms) / 60_000);
    if (minutes < 1) return 'gerade eben';
    if (minutes < 60) return `vor ${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `vor ${hours} h`;
    const days = Math.floor(hours / 24);
    if (days < 30) return `vor ${days} d`;
    return new Date(ms).toLocaleDateString('en-US');
  });
}
