import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import type { CliModelInfo } from '../../../../cli';
import {
  WikiGradingRunState,
  WikiGradingRunStatus,
  WikiPulseCritical,
  WikiPulseCriticalItem,
} from '../../../../../models/project-docs.model';

type WikiGradeTone = 'good' | 'info' | 'warn' | 'bad' | 'muted';

/** Status-indicator tone of a grading run state; a state added later stays neutral. */
const GRADING_STATE_TONES: Readonly<Record<WikiGradingRunState, StatusTone>> = {
  running: 'running',
  completed: 'success',
  aborted: 'neutral',
  failed: 'danger',
};

export function gradingStateTone(state: WikiGradingRunState | string | null | undefined): StatusTone {
  return (state && GRADING_STATE_TONES[state as WikiGradingRunState]) || 'neutral';
}

/**
 * The wiki-grading trigger + critical-pages surface (AGT-2051), rendered under
 * the Pulse landing view. Purely presentational: the parent owns the maintenance
 * model default, the run status, and the CLI calls (start / abort / poll); this
 * component only renders the model picker, the progress bar, and the LLM
 * critical-pages list, and emits intent. Split out of {@link WikiPulseComponent}
 * so each component stays within its size budget.
 */
@Component({
  selector: 'app-wiki-grade-panel',
  standalone: true,
  imports: [StatusIndicatorComponent, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './wiki-grade-panel.component.html',
  styleUrl: './wiki-grade-panel.component.scss',
})
export class WikiGradePanelComponent {
  readonly gradingStatus = input<WikiGradingRunStatus | null>(null);
  readonly gradeModel = input<string | null>(null);
  readonly gradeLevel = input<string | null>(null);
  readonly gradeModels = input<readonly CliModelInfo[]>([]);
  readonly critical = input<WikiPulseCritical | null>(null);

  readonly startGrading = output<void>();
  readonly abortGrading = output<void>();
  readonly gradeModelChange = output<string>();
  readonly gradeLevelChange = output<string | null>();
  /** Emits a page relPath to open straight to its companion report tab. */
  readonly openReport = output<string>();

  readonly grading = computed(() => this.gradingStatus());
  readonly gradingRunning = computed(() => this.grading()?.state === 'running');
  readonly gradingProgressPct = computed(() => {
    const g = this.grading();
    if (!g || g.total === 0) return 0;
    return Math.min(100, Math.round((g.processed / g.total) * 100));
  });

  /** Thinking levels the selected model supports (drives the optional level select). */
  readonly gradeLevelOptions = computed<readonly string[]>(() => {
    const id = this.gradeModel();
    return this.gradeModels().find(m => m.id === id)?.thinkingLevels ?? [];
  });

  onModelSelect(ev: Event): void {
    this.gradeModelChange.emit((ev.target as HTMLSelectElement).value);
  }

  onLevelSelect(ev: Event): void {
    const value = (ev.target as HTMLSelectElement).value;
    this.gradeLevelChange.emit(value || null);
  }

  openCritical(item: WikiPulseCriticalItem): void {
    this.openReport.emit(item.relPath);
  }

  /** Tone for an LLM grade (A good, B info, C warn, D bad). */
  criticalTone(grade: string | null | undefined): WikiGradeTone {
    switch ((grade ?? '').toUpperCase()) {
      case 'A': return 'good';
      case 'B': return 'info';
      case 'C': return 'warn';
      case 'D': return 'bad';
      default: return 'muted';
    }
  }

  readonly gradingStateTone = gradingStateTone;

  /** Short human label for the current grading run state. */
  gradingStateLabel(): string {
    const g = this.grading();
    if (!g) return '';
    switch (g.state) {
      case 'running': return `Grading ${g.processed}/${g.total}`;
      case 'completed': return `Graded ${g.graded}, skipped ${g.skipped}, ${g.critical} critical`;
      case 'aborted': return `Aborted after ${g.processed}/${g.total}`;
      case 'failed': return g.error ? `Failed: ${g.error}` : 'Run failed';
      default: return '';
    }
  }
}
