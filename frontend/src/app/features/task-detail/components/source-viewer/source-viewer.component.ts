import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { OverlayPortalDirective } from '@studio/ui/directives/overlay-portal.directive';
import { TaskService } from '../../../../services/task.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { detectLanguage } from '../protocol-pane/run-git-viewer/diff-utils';
import { highlightBlock } from '../beautiful-results/highlight-lazy';
import { splitHighlightedLines, splitPlainLines } from './source-highlight';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';

export interface SourceViewerRequest {
  path: string;
  line: number | null;
}

interface SourceLine {
  num: number;
  html: string;
}

// Above this size we skip the syntax-highlight pass (and render plain,
// escaped lines) so opening a huge generated file doesn't jank the overlay.
const HIGHLIGHT_CHAR_LIMIT = 200_000;

/**
 * Read-only source viewer overlay. Opened from a clickable source reference
 * in a protocol/result body (see beautiful-results `openSource`). Fetches the
 * live file through the API with `scope: 'code'` (arbitrary repo file,
 * repo-root guarded by the backend), renders it line-numbered with lazy
 * syntax highlighting, and scrolls to the referenced line.
 */
@Component({
  selector: 'app-source-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [OverlayPortalDirective, AppTooltipDirective],
  templateUrl: './source-viewer.component.html',
  styleUrl: './source-viewer.component.scss',
})
export class SourceViewerComponent {
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly request = input<SourceViewerRequest | null>(null);

  readonly closeRequest = output<void>();

  readonly state = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly error = signal<string | null>(null);
  readonly lines = signal<SourceLine[]>([]);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  private content = '';

  readonly path = computed(() => this.request()?.path ?? null);
  readonly activeLine = computed(() => this.request()?.line ?? null);
  readonly visible = computed(() => this.request() !== null);
  readonly fileName = computed(() => {
    const p = this.path();
    return p ? p.split('/').pop() || p : '';
  });

  private readonly tasks = inject(TaskService);
  @ViewChild('bodyRef') private bodyRef?: ElementRef<HTMLElement>;
  private loadKey = '';

  constructor() {
    // Load when the requested path (or job/watchPath) changes; a same-file
    // request with a different line does not reload, only re-scrolls.
    effect(() => {
      const req = this.request();
      const job = this.jobId();
      const key = req && job ? `${job}::${this.watchPath() ?? ''}::${req.path}` : '';
      if (key && key !== this.loadKey) {
        this.loadKey = key;
        this.load(req!.path);
      } else if (!key) {
        this.loadKey = '';
      }
    });

    // Re-scroll whenever the active line or the rendered lines change.
    effect(() => {
      this.activeLine();
      this.lines();
      this.scrollToActive();
    });
  }

  copyLabel(): string {
    const s = this.copyState();
    return s === 'copied' ? '✓ Copied' : s === 'failed' ? '⚠ Failed' : 'Copy';
  }

  copyContent(): void {
    if (!this.content) return;
    void copyTextToClipboard(this.content).then((ok) => {
      this.copyState.set(ok ? 'copied' : 'failed');
      setTimeout(() => this.copyState.set('idle'), 1600);
    });
  }

  private load(path: string): void {
    const job = this.jobId();
    if (!job) return;
    const key = this.loadKey;
    this.state.set('loading');
    this.error.set(null);
    this.lines.set([]);
    this.content = '';
    this.tasks.readTaskFile(job, path, this.watchPath() ?? undefined, 'code').subscribe({
      next: (text) => {
        if (this.loadKey !== key) return;
        this.content = (text ?? '').replace(/\r\n/g, '\n');
        this.state.set('loaded');
        void this.buildLines(this.content, path, key);
      },
      error: (err) => {
        if (this.loadKey !== key) return;
        this.error.set(err?.error?.error || err?.message || 'Could not load file.');
        this.state.set('error');
      },
    });
  }

  private async buildLines(text: string, path: string, key: string): Promise<void> {
    // Instant first paint with plain escaped lines.
    this.lines.set(splitPlainLines(text).map((html, i) => ({ num: i + 1, html })));

    const lang = detectLanguage(path);
    if (!lang || text.length > HIGHLIGHT_CHAR_LIMIT) return;
    const { html } = await highlightBlock(text, lang);
    if (this.loadKey !== key) return; // a newer file won the race
    this.lines.set(splitHighlightedLines(html).map((h, i) => ({ num: i + 1, html: h })));
  }

  private scrollToActive(): void {
    const line = this.activeLine();
    if (!line || this.lines().length === 0) return;
    queueMicrotask(() => {
      const host = this.bodyRef?.nativeElement;
      const row = host?.querySelector<HTMLElement>(`[data-line="${line}"]`);
      row?.scrollIntoView({ block: 'center' });
    });
  }
}
