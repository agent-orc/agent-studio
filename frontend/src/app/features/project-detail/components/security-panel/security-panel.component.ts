import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { SecurityService } from '../../../../services/security.service';
import {
  SecurityBaselineBadge,
  SecurityBaselineResponse,
  SecurityReviewListResponse,
  SecurityReviewSummary,
} from './security-panel.types';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { baselineTone, historyVerdictTone, verdictTone } from './security-panel-status';

/**
 * Project Security panel (slice 1 of the quality-system mockup,
 * docs/mockups/quality-system/). Loads the baseline and review history
 * for one project and renders:
 *
 * <list type="bullet">
 *   <item>Header: title + baseline badge.</item>
 *   <item>Cards row: last review verdict, open-findings split, baseline definition link.</item>
 *   <item>Action buttons: Run security audit, Open evidence, Create follow-up task.</item>
 *   <item>Review history: newest first; <c>parseOk=false</c> rows show the
 *   "unstructured report" warning so the user still has a link to the
 *   evidence file (Report Contracts in the mockup README).</item>
 * </list>
 *
 * Action-driven principle: this component does NO analysis on its own
 * (per the mockup README). It only reads existing files via the
 * <c>SecurityService</c>; the "Run security audit" button delegates to
 * the backend, which queues a normal job that the orchestrator picks up.
 */
@Component({
  selector: 'app-security-panel',
  standalone: true,
  imports: [ConceptHelpComponent, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './security-panel.component.html',
  styleUrl: './security-panel.component.scss',
})
export class SecurityPanelComponent {
  private readonly security = inject(SecurityService);

  readonly projectName = input.required<string>();

  /** Emits when the user clicks "Create follow-up task". The host opens the create-job dialog. */
  readonly createFollowUp = output<{ projectName: string; prefill: string }>();

  /** Emits when "Open evidence" is clicked, with the relative path to the most recent review file. */
  readonly openEvidence = output<{ projectName: string; relPath: string }>();

  /** Emits when an audit is successfully queued; host can refresh the kanban or surface a chip. */
  readonly auditQueuedEvent = output<{ projectName: string; jobId: string }>();

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly reviews = signal<SecurityReviewSummary[]>([]);
  readonly baseline = signal<SecurityBaselineResponse | null>(null);
  readonly auditBusy = signal<boolean>(false);
  readonly auditError = signal<string | null>(null);
  readonly auditQueued = signal<string | null>(null);
  /** Lazy-loaded raw Markdown bodies for parseOk=false rows, keyed by file name. */
  readonly rawCache = signal<Record<string, string>>({});

  readonly lastReview = computed<SecurityReviewSummary | null>(() => {
    const list = this.reviews();
    return list.length > 0 ? list[0] : null;
  });

  readonly baselineBadge = computed<SecurityBaselineBadge>(() => {
    const b = this.baseline();
    if (!b) return 'unknown';
    if (!b.exists) return 'missing';
    const status = (b.status ?? '').toLowerCase();
    if (!status) return 'unknown';
    if (status.includes('ok') || status.includes('pass')) return 'ok';
    if (status.includes('stale') || status.includes('warn')) return 'stale';
    if (status.includes('fail') || status.includes('crit')) return 'failing';
    return 'unknown';
  });

  readonly baselineTone = computed(() => baselineTone(this.baselineBadge()));

  readonly baselineBadgeLabel = computed<string>(() => {
    switch (this.baselineBadge()) {
      case 'ok': return 'Baseline OK';
      case 'stale': return 'Baseline stale';
      case 'failing': return 'Baseline failing';
      case 'missing': return 'No baseline';
      default: return 'Baseline unknown';
    }
  });

  readonly severitySplitEntries = computed(() => {
    const last = this.lastReview();
    if (!last?.severities) return [];
    const order = ['critical', 'high', 'medium', 'low'];
    const entries: { label: string; count: number; tone: string }[] = [];
    for (const key of order) {
      const value = last.severities[key];
      if (typeof value === 'number') entries.push({ label: key, count: value, tone: key });
    }
    // Append any extra severities the producer added (e.g. "info") so the
    // panel stays open to schema growth without losing data.
    for (const [key, value] of Object.entries(last.severities)) {
      if (!order.includes(key.toLowerCase())) {
        entries.push({ label: key, count: value, tone: 'medium' });
      }
    }
    return entries;
  });

  constructor() {
    // Reload whenever the project changes. The shell may switch projects
    // without unmounting the panel (rare today, but keeps the contract
    // correct if a future router does in-place navigation).
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.auditError.set(null);
    this.auditQueued.set(null);
    this.rawCache.set({});

    this.security.listReviews(name).subscribe({
      next: (res: SecurityReviewListResponse) => {
        this.reviews.set(res.reviews);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.message ?? 'unknown');
        this.reviews.set([]);
      },
    });

    this.security.getBaseline(name).subscribe({
      next: (b: SecurityBaselineResponse) => {
        this.baseline.set(b);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.baseline.set(null);
        this.loading.set(false);
      },
    });
  }

  onRunAudit(): void {
    const name = this.projectName();
    if (!name) return;
    if (this.auditBusy()) return;
    this.auditBusy.set(true);
    this.auditError.set(null);
    this.auditQueued.set(null);
    this.security.queueAudit(name).subscribe({
      next: (res) => {
        this.auditBusy.set(false);
        this.auditQueued.set(`Audit queued (${res.jobId}). It will start when the runner is idle.`);
        this.auditQueuedEvent.emit({ projectName: name, jobId: res.jobId });
      },
      error: (err: HttpErrorResponse) => {
        this.auditBusy.set(false);
        const body = err.error;
        const reason = body?.message ?? body?.error ?? err.message ?? 'audit failed';
        this.auditError.set(reason);
      },
    });
  }

  onOpenEvidence(): void {
    const last = this.lastReview();
    const name = this.projectName();
    if (!last || !name) return;
    this.openEvidence.emit({ projectName: name, relPath: last.relPath });
  }

  onCreateFollowUp(): void {
    const name = this.projectName();
    const last = this.lastReview();
    const lines: string[] = ['# Security follow-up', ''];
    if (last) {
      lines.push(`Source review: \`${last.relPath}\`${last.reviewDate ? ' (' + last.reviewDate + ')' : ''}.`);
      if (last.summary) {
        lines.push('');
        lines.push(last.summary);
      }
      if (typeof last.openFindings === 'number') {
        lines.push('');
        lines.push(`Open findings reported: ${last.openFindings}.`);
      }
    } else {
      lines.push('No prior review attached. Describe the finding before queueing.');
    }
    lines.push('');
    lines.push('Action: address the finding above. Treat the source review as evidence; do not silently mutate task state.');
    this.createFollowUp.emit({ projectName: name, prefill: lines.join('\n') });
  }

  loadRaw(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.rawCache()[fileName] !== undefined) return;
    this.security.readReview(name, fileName).subscribe({
      next: (res) => {
        const next = { ...this.rawCache(), [fileName]: res.content };
        this.rawCache.set(next);
      },
      error: (err: HttpErrorResponse) => {
        const next = { ...this.rawCache(), [fileName]: `Failed to load: ${err.message ?? 'unknown'}` };
        this.rawCache.set(next);
      },
    });
  }

  formatDate(iso: string): string {
    if (!iso) return '';
    // Display the date portion only; the panel header already shows
    // "Loading…" for in-flight states, so timestamps in cards stay terse.
    const idx = iso.indexOf('T');
    return idx > 0 ? iso.slice(0, idx) : iso;
  }

  /** Status tone of a free-text review verdict (AGT-3403). */
  readonly verdictTone = verdictTone;
  /** History rows: a past failure is not acute (R4). */
  readonly historyVerdictTone = historyVerdictTone;
}
