import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';

import {
  TaskInfo,
  ReviewEvidenceEntry,
  ReviewEvidenceSeverity,
} from '../../../../../models/task.model';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatDateTimeUtc } from '../../../../../services/format.util';
import { MediaLightboxService } from '../../../../../services/media-lightbox.service';
import { resolveProtocolImageSrc } from '../protocol-image-resolver';
import { StatusIndicatorComponent, type StatusTone } from '../../../../../components/status-indicator/status-indicator.component';

const SEVERITY_TONES: Readonly<Record<ReviewEvidenceSeverity, StatusTone>> = {
  high: 'danger',
  warn: 'warning',
  info: 'info',
};

/** Tone of a review finding severity (AGT-3403); unknown severities stay neutral. */
export function reviewEvidenceSeverityTone(severity: string): StatusTone {
  return SEVERITY_TONES[severity as ReviewEvidenceSeverity] ?? 'neutral';
}

/**
 * Extensions we render inline as a thumbnail instead of a bare path. The
 * screenshot-harvest pipeline drops PNGs into `results/`; other producers
 * attach JPEG/WebP/GIF. Anything else stays a labelled text reference.
 */
const IMAGE_REF_RE = /\.(png|jpe?g|webp|gif|avif|bmp)$/i;

/**
 * Renders the per-task **review evidence** panel: findings from security
 * audits, code-review passes, task checks, or human notes that landed in
 * the job's `results/review-evidence.jsonl` file. The panel is purely
 * advisory — these findings are never blockers for state transitions.
 *
 * Each finding renders as a row with:
 *   - severity chip (info / warn / high),
 *   - source label, timestamp, run index when available,
 *   - title + body,
 *   - linked artifacts / file references,
 *   - "Acknowledge" toggle,
 *   - "Create follow-up task" action that posts to the API and emits
 *     the new job id so the parent can navigate.
 *
 * The component is presentational: data comes in via @Input, state changes
 * leave via @Output. The parent owns API calls and the routing decision
 * after a follow-up is created.
 */
@Component({
  selector: 'app-review-evidence-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, StatusIndicatorComponent],
  templateUrl: './review-evidence-panel.component.html',
  styleUrl: './review-evidence-panel.component.scss',
})
export class ReviewEvidencePanelComponent {
  private readonly lightbox = inject(MediaLightboxService);

  readonly entries = input.required<ReviewEvidenceEntry[]>();
  readonly job = input.required<TaskInfo>();

  readonly acknowledge = output<{ entry: ReviewEvidenceEntry; acknowledged: boolean }>();
  readonly createFollowup = output<ReviewEvidenceEntry>();

  /** Id of the row whose action is currently in flight (disables both buttons). */
  readonly busyId = signal<string | null>(null);

  /**
   * Stable order: high severity first, then warn, then info; ties broken by
   * createdAt ascending so the user reads findings chronologically inside a
   * severity bucket.
   */
  sorted = computed<ReviewEvidenceEntry[]>(() => {
    const rank: Record<ReviewEvidenceSeverity, number> = { high: 0, warn: 1, info: 2 };
    return [...this.entries()].sort((a, b) => {
      const ra = rank[a.severity] ?? 3;
      const rb = rank[b.severity] ?? 3;
      if (ra !== rb) return ra - rb;
      return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
    });
  });

  readonly severityTone = reviewEvidenceSeverityTone;

  severityLabel(s: ReviewEvidenceSeverity): string {
    if (s === 'high') return 'HIGH';
    if (s === 'warn') return 'WARN';
    return 'INFO';
  }

  sourceLabel(s: string): string {
    switch (s) {
      case 'security-audit':
        return 'Security audit';
      case 'code-review':
        return 'Code review';
      case 'task-check':
        return 'Task check';
      case 'human-note':
        return 'Human note';
      default:
        return 'Other';
    }
  }

  formatTime(iso: string): string {
    return formatDateTimeUtc(iso);
  }

  /** True when a reference points to a bitmap image we can thumbnail. */
  isImageRef(ref: string): boolean {
    return IMAGE_REF_RE.test((ref ?? '').trim());
  }

  /**
   * A type-specific glyph for a non-image reference so the leading icon
   * actually distinguishes markdown / json / log / config from a plain
   * file, instead of the old generic paperclip/page glyph.
   */
  refIcon(ref: string): string {
    const p = (ref ?? '').trim().toLowerCase();
    if (/\.(md|markdown)$/.test(p)) return '📝';
    if (/\.jsonl?$/.test(p)) return '🧾';
    if (/\.(log|txt|out|err)$/.test(p)) return '📋';
    if (/\.(ya?ml|toml|ini|env|cfg|conf)$/.test(p)) return '⚙️';
    if (/\.(csv|tsv)$/.test(p)) return '📊';
    if (/\.(cs|ts|js|py|go|rs|java|rb|cpp|c|h|scss|css|html)(:\d+)?$/.test(p)) return '〈〉';
    return '📄';
  }

  /** Short basename shown under a thumbnail (the full path lives in the tooltip). */
  baseName(ref: string): string {
    const p = (ref ?? '').trim().replace(/[/\\]+$/, '');
    const i = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
    return i >= 0 ? p.slice(i + 1) : p;
  }

  /** Resolve a `results/` or `attachments/` ref to the API URL that serves it. */
  thumbUrl(ref: string): string {
    const job = this.job();
    return resolveProtocolImageSrc((ref ?? '').trim(), job.id, job.watchPath);
  }

  /**
   * Image references for an entry, artifacts first then file refs, matching
   * the render order so the lightbox opens on the thumbnail that was clicked.
   */
  imageRefs(e: ReviewEvidenceEntry): string[] {
    return [...e.artifacts, ...e.fileRefs].filter((r) => this.isImageRef(r));
  }

  /** Non-image artifacts, kept as labelled text rows. */
  textArtifacts(e: ReviewEvidenceEntry): string[] {
    return e.artifacts.filter((r) => !this.isImageRef(r));
  }

  /** Non-image file references, kept as labelled text rows. */
  textFileRefs(e: ReviewEvidenceEntry): string[] {
    return e.fileRefs.filter((r) => !this.isImageRef(r));
  }

  /** Open the shared media lightbox as a gallery of this entry's images. */
  openImage(e: ReviewEvidenceEntry, clicked: string): void {
    const refs = this.imageRefs(e);
    const images = refs.map((r) => ({ src: this.thumbUrl(r), alt: r }));
    const index = Math.max(0, refs.indexOf(clicked));
    this.lightbox.openGallery({ images, index });
  }

  onToggleAck(e: ReviewEvidenceEntry): void {
    if (this.busyId()) return;
    this.busyId.set(e.id);
    this.acknowledge.emit({ entry: e, acknowledged: !e.acknowledged });
  }

  onCreateFollowup(e: ReviewEvidenceEntry): void {
    if (this.busyId() || e.followupJobId) return;
    this.busyId.set(e.id);
    this.createFollowup.emit(e);
  }

  /** Parent calls this once its API request resolves so the row re-enables. */
  clearBusy(): void {
    this.busyId.set(null);
  }
}
