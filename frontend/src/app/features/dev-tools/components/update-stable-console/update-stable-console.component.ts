import { AfterViewChecked, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, signal, ViewChild, output } from '@angular/core';

import { OverlayPortalDirective } from '@studio/ui/directives/overlay-portal.directive';
import { TooltipDirective } from 'coding-agent-chat/shared';
interface ConsoleLine {
  kind: 'log' | 'stdout' | 'stderr' | 'error' | 'done';
  text: string;
}

/**
 * Modal console that streams update-stable.sh via SSE
 * (GET /api/devtools/update-stable/stream). One server event = one line;
 * we push lines into a signal and pin the scroll to the bottom.
 *
 * Closing the dialog mid-run aborts the EventSource which closes the HTTP
 * stream; the backend kills the child process when the request is cancelled.
 */
@Component({
  selector: 'app-update-stable-console',
  standalone: true,
  imports: [TooltipDirective, OverlayPortalDirective],
  templateUrl: './update-stable-console.component.html',
  styleUrl: './update-stable-console.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UpdateStableConsoleComponent implements OnDestroy, AfterViewChecked {
  readonly closed = output<void>();

  readonly lines = signal<ConsoleLine[]>([]);
  readonly running = signal(false);
  readonly exitCode = signal<number | null>(null);

  @ViewChild('log') logEl?: ElementRef<HTMLPreElement>;

  private source: EventSource | null = null;
  private prevLineCount = 0;

  ngOnDestroy(): void {
    this.abort();
  }

  ngAfterViewChecked(): void {
    if (!this.logEl) return;
    const count = this.lines().length;
    if (count === this.prevLineCount) return;
    this.prevLineCount = count;
    const el = this.logEl.nativeElement;
    el.scrollTop = el.scrollHeight;
  }

  onBackdropClick(): void {
    if (this.running()) return;
    this.closed.emit();
  }

  start(): void {
    if (this.running()) return;
    this.lines.set([]);
    this.exitCode.set(null);
    this.running.set(true);

    const src = new EventSource('/api/devtools/update-stable/stream');
    this.source = src;

    const push = (kind: ConsoleLine['kind']) => (e: MessageEvent) => {
      this.lines.update((arr) => [...arr, { kind, text: e.data }]);
    };
    src.addEventListener('log', push('log'));
    src.addEventListener('stdout', push('stdout'));
    src.addEventListener('stderr', push('stderr'));
    src.addEventListener('error', (e: MessageEvent) => {
      // Both protocol-level errors (no event payload) and our `event: error`
      // frames land here; `e.data` is only set for the latter.
      if (e.data) this.lines.update((arr) => [...arr, { kind: 'error', text: e.data }]);
      this.finish(null);
    });
    src.addEventListener('done', (e: MessageEvent) => {
      this.lines.update((arr) => [...arr, { kind: 'done', text: e.data }]);
      const m = /exit code (-?\d+)/.exec(e.data);
      this.finish(m ? Number(m[1]) : null);
    });
  }

  abort(): void {
    if (this.source) {
      this.source.close();
      this.source = null;
    }
    this.running.set(false);
  }

  clear(): void {
    this.lines.set([]);
    this.exitCode.set(null);
  }

  private finish(exit: number | null): void {
    if (this.source) { this.source.close(); this.source = null; }
    this.running.set(false);
    this.exitCode.set(exit);
  }
}
