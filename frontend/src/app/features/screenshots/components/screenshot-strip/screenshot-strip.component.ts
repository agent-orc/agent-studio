import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import type { TaskScreenshot } from '../../../../features/screenshots';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ModalStackService } from '../../../../services/modal-stack.service';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Screenshot test status to status tone (AGT-3403); unknown states are neutral. */
const SCREENSHOT_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  passed: 'success',
  failed: 'danger',
  skipped: 'neutral',
};

export function screenshotStatusTone(status: string | null | undefined): StatusTone {
  return SCREENSHOT_STATUS_TONES[status ?? ''] ?? 'neutral';
}
/**
 * Visual-evidence strip + lightbox. Two surfaces share this component:
 *
 * 1. The per-task strip in the protocol pane, populated from
 *    `GET /api/tasks/{id}/screenshots` (every image under
 *    `<job>/results/`, including the harvested Playwright artefacts).
 * 2. The workspace "Visual evidence" reel, populated from
 *    `GET /api/workspace/screenshots` and grouped by hour bucket on
 *    the rendering side; entries are clickable thumbnails that link
 *    back to the originating task page.
 *
 * The strip itself is only rendered when at least one screenshot is
 * present; this is enforced at the host site (no in-component "empty
 * state" placeholder, per the task contract).
 *
 * Lightbox is owned here. Prev/next navigation cycles deterministically
 * (wraps at both ends) so the keyboard shortcut `←` / `→` is always
 * meaningful. `Esc` closes. The "Open in Explorer" affordance copies
 * the absolute on-disk path to the clipboard since the browser cannot
 * launch Explorer directly.
 */
@Component({
  selector: 'app-screenshot-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, StatusIndicatorComponent],
  templateUrl: './screenshot-strip.component.html',
  styleUrl: './screenshot-strip.component.scss',
})
export class ScreenshotStripComponent {
  readonly screenshots = input.required<TaskScreenshot[]>();
  /** 'task' = per-task strip in protocol pane; 'reel' = workspace reel. */
  readonly variant = input<'task' | 'reel'>('task');

  readonly openTask = output<TaskScreenshot>();

  readonly activeIndex = signal<number>(-1);

  readonly active = computed(() =>
    this.activeIndex() >= 0 && this.activeIndex() < this.screenshots().length
      ? this.activeIndex()
      : null,
  );

  readonly current = computed<TaskScreenshot | null>(() => {
    const i = this.activeIndex();
    if (i < 0) return null;
    const all = this.screenshots();
    return i < all.length ? all[i] : null;
  });

  readonly ariaLabel = computed(() =>
    this.variant() === 'reel' ? 'Workspace visual evidence reel' : 'Task screenshots',
  );

  readonly pathCopyState = signal<'idle' | 'copied' | 'failed'>('idle');

  pathButtonLabel(): string {
    const s = this.pathCopyState();
    if (s === 'copied') return 'Path copied';
    if (s === 'failed') return 'Copy failed';
    return 'Open in Explorer';
  }

  openLightbox(index: number): void {
    this.activeIndex.set(index);
  }

  close(): void {
    this.activeIndex.set(-1);
    this.pathCopyState.set('idle');
  }

  prev(): void {
    const total = this.screenshots().length;
    if (total === 0) return;
    const cur = this.activeIndex();
    this.activeIndex.set((cur - 1 + total) % total);
    this.pathCopyState.set('idle');
  }

  next(): void {
    const total = this.screenshots().length;
    if (total === 0) return;
    const cur = this.activeIndex();
    this.activeIndex.set((cur + 1) % total);
    this.pathCopyState.set('idle');
  }

  thumbTitle(s: TaskScreenshot): string {
    const ts = formatLocalDateTime(s.timestampUtc);
    const lines = [s.caption, s.fileName, ts];
    if (s.projectName) lines.push(s.projectName);
    if (s.status) lines.push(`Status: ${this.statusLabel(s.status)}`);
    const source = this.sourceLabel(s);
    if (source) lines.push(`Source: ${source}`);
    return lines.filter(Boolean).join('\n');
  }

  /**
   * Human label for the provenance chip, or `''` when the source is
   * `unlabeled` so the UI never claims a source it cannot prove. A composite
   * spells out its part sources when known.
   */
  sourceLabel(s: TaskScreenshot): string {
    switch (s.source) {
      case 'real':
        return 'real';
      case 'mocked':
        return 'mocked';
      case 'composite':
        return s.compositeParts.length > 0
          ? `composite (${s.compositeParts.join(', ')})`
          : 'composite';
      case 'pinned':
        return 'pinned';
      default:
        return '';
    }
  }

  sourceTooltip(s: TaskScreenshot): string {
    switch (s.source) {
      case 'real':
        return 'Captured against a running backend';
      case 'mocked':
        return 'Captured from an e2e run with mocked API routes';
      case 'composite':
        return s.compositeParts.length > 0
          ? `Stitched composite combining ${s.compositeParts.join(' + ')} parts`
          : 'Stitched composite image';
      case 'pinned':
        return 'Captured from the versioned deterministic documentation workspace';
      default:
        return '';
    }
  }

  readonly statusTone = screenshotStatusTone;

  statusLabel(status: string): string {
    switch (status) {
      case 'passed':
        return 'Passed';
      case 'failed':
        return 'Failed';
      case 'skipped':
        return 'Skipped';
      default:
        return 'Unknown';
    }
  }

  formatTimestamp(iso: string): string {
    return formatLocalDateTime(iso);
  }

  async copyLocalPath(s: TaskScreenshot): Promise<void> {
    if (!s.localPath) return;
    const ok = await copyTextToClipboard(s.localPath);
    this.pathCopyState.set(ok ? 'copied' : 'failed');
    setTimeout(() => this.pathCopyState.set('idle'), 1800);
  }

  onOpenTask(s: TaskScreenshot): void {
    this.openTask.emit(s);
  }

  // Arrow keys for the lightbox stay local. This runs as a capture-phase
  // listener because the task detail pager listens on document keydown; a
  // normal window HostListener fires too late in the bubble path.
  onKey(e: KeyboardEvent): void {
    if (this.activeIndex() < 0) return;
    if (e.key === 'ArrowLeft') {
      this.prev();
      e.preventDefault();
      e.stopPropagation();
    } else if (e.key === 'ArrowRight') {
      this.next();
      e.preventDefault();
      e.stopPropagation();
    }
  }

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private lightboxStackDispose: (() => void) | null = null;
  private readonly preloadedUrls = new Set<string>();
  private readonly keydownCapture = (event: KeyboardEvent) => this.onKey(event);

  constructor() {
    if (typeof document !== 'undefined') {
      document.addEventListener('keydown', this.keydownCapture, true);
    }

    // Lightbox open/close drives the modal-stack registration. The strip
    // itself is never the top of the stack - only its lightbox is.
    effect(() => {
      const open = this.activeIndex() >= 0;
      if (open) {
        if (!this.lightboxStackDispose) {
          this.lightboxStackDispose = this.modalStack.push('screenshot-lightbox', () =>
            this.close(),
          );
        }
      } else if (this.lightboxStackDispose) {
        this.lightboxStackDispose();
        this.lightboxStackDispose = null;
      }
    });
    effect(() => {
      const activeIndex = this.activeIndex();
      const screenshots = this.screenshots();
      if (activeIndex < 0 || screenshots.length < 2) return;

      this.preloadNeighbour(activeIndex - 1, screenshots);
      this.preloadNeighbour(activeIndex + 1, screenshots);
    });
    this.destroyRef.onDestroy(() => {
      if (typeof document !== 'undefined') {
        document.removeEventListener('keydown', this.keydownCapture, true);
      }
      if (this.lightboxStackDispose) {
        this.lightboxStackDispose();
        this.lightboxStackDispose = null;
      }
    });
  }

  private preloadNeighbour(index: number, screenshots: TaskScreenshot[]): void {
    const normalized = (index + screenshots.length) % screenshots.length;
    const url = screenshots[normalized]?.url;
    if (!url || this.preloadedUrls.has(url) || typeof Image === 'undefined') return;

    this.preloadedUrls.add(url);
    const img = new Image();
    img.decoding = 'async';
    img.src = url;
  }
}

function formatLocalDateTime(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  // Locale-neutral compact form: YYYY-MM-DD HH:mm
  const pad = (n: number) => String(n).padStart(2, '0');
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}`
  );
}
