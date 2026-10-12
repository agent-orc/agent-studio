import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { SummaryOutcome, TaskDetail } from '../../../../../models/task.model';
import { laneTone } from '../../../../../models/lane-presentation';
import type { ProtocolVerdict } from '../protocol-verdict';
import { buildResultDocument } from '../result-document';
import { RESULT_CASE_META } from '../result-case';
import { BeautifulResultsComponent } from '../../beautiful-results/beautiful-results.component';
import type { GeneratedFileProvenanceView } from '../../generated-file-provenance.util';
import { TestEvidenceStatusComponent } from '../../../../test-evidence';

const SUMMARY_OUTCOME_LABELS: Record<SummaryOutcome, string> = {
  'delivered': 'delivered',
  'delivered-via-operator-merge': 'delivered via operator merge',
  'parked-on-decision': 'parked on a decision',
  'failed': 'failed',
  'no-op': 'no change needed',
};

/**
 * The Result view (Protocol -> Result redesign).
 *
 * Renders a finished run in the layered, shareable shape the operator asked
 * for, top to bottom:
 *
 *   1. **summary head** - the single emphasized outcome plus quiet grade /
 *      duration / tokens / commit stats, so "is this fine and how big is it?"
 *      answers at a glance;
 *   2. **overview** - a "problem -> solution" card with case-tuned labels,
 *      the one thing worth sharing;
 *   3. **detail** - the existing rich markdown body (What Was Done / Open
 *      Items / Notes / Images), delegated to <app-beautiful-results> so the
 *      redesign adds zero regression to source links, diffs, and image
 *      lightboxes.
 *
 * All structure/parse logic lives in the pure {@link buildResultDocument};
 * this component is a thin, OnPush projection of that document. Backward
 * compatible by construction: it builds the document from `status.md` + task
 * metadata, so every historical run renders without a backend change.
 */
@Component({
  selector: 'app-result-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, BeautifulResultsComponent, TestEvidenceStatusComponent],
  templateUrl: './result-view.component.html',
  styleUrl: './result-view.component.scss',
})
export class ResultViewComponent {
  readonly detail = input.required<TaskDetail>();
  readonly verdict = input.required<ProtocolVerdict>();
  readonly provenance = input<GeneratedFileProvenanceView | null>(null);
  readonly copyLabel = input('Copy');

  /** Bubbled up from the detail body so the host opens the source viewer. */
  readonly openSource = output<{ path: string; line: number | null }>();
  readonly openWiki = output<string>();
  readonly openTask = output<string>();
  readonly navigateMetric = output<string>();
  readonly openDocument = output<string>();
  readonly copyRequested = output<void>();
  readonly moreActions = output<MouseEvent>();

  readonly doc = computed(() => buildResultDocument(this.detail(), this.verdict()));
  readonly caseMeta = computed(() => RESULT_CASE_META[this.doc().case.case]);
  readonly textScanHint = computed(() => this.verdict().signals.find(
    signal => signal.sourceLabel === 'text scan' && signal.label.startsWith('status text mentions:'),
  ) ?? null);
  readonly outcomeTone = computed(() => {
    const status = this.verdict().status;
    if (status === 'failed') return 'problem';
    if (status === 'needs-decision') return 'warn';
    if (status === 'succeeded') return 'accent';
    return 'neutral';
  });

  /**
   * Lane tone when the lane itself is the leading signal, else null. A card
   * parked in Human review then shows the Result header dot in the lane's own
   * colour, matching the header chip and the board column, instead of the
   * generic amber "needs-decision" tone (AGT-2715).
   */
  readonly laneTone = computed(() => {
    const lane = this.verdict().lane;
    return lane ? laneTone(lane) : null;
  });

  /**
   * Contradictions the closing Result assessment found between the card's
   * signals (AGT-3046). Empty means no block: a clean card shows nothing.
   */
  readonly inconsistencies = computed(() => this.detail().summaryAssessment?.inconsistencies ?? []);
  readonly assessmentOutcome = computed(() => {
    const outcome = this.detail().summaryAssessment?.outcome;
    return outcome ? SUMMARY_OUTCOME_LABELS[outcome] ?? outcome : null;
  });
  readonly anyWorkMayBeLost = computed(() => this.inconsistencies().some(item => item.workMayBeLost));

  readonly latestAspectVerdicts = computed(() =>
    this.detail().info.reviewProjection?.attempts?.[0]?.aspects ?? []);

  openResultReference(reference: { path: string; line: number | null }): void {
    const fileName = reference.path.split('/').pop() ?? reference.path;
    if (/^(?:aspect-[^/]+\.md|remote-review-(?:grade-)?[^/]+)$/i.test(fileName)) {
      this.openDocument.emit(fileName);
      return;
    }
    this.openSource.emit(reference);
  }
}
