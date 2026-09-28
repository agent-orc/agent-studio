import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, input, signal, computed, effect, OnDestroy } from '@angular/core';
import { CliOutputLine } from '../../../../models/task.model';

@Component({
  selector: 'app-cli-console',
  standalone: true,
  templateUrl: './cli-console.html',
  styleUrl: './cli-console.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CliConsoleComponent implements OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly title = input('Console Output');
  readonly bodyMaxHeight = input('400px');
  readonly filterStream = signal<'all' | 'stdout' | 'stderr'>('all');
  readonly autoScroll = signal(true);
  readonly filteredLines = computed(() => {
    const all = this.lines();
    const filter = this.filterStream();
    return filter === 'all' ? all : all.filter((line) => line.stream === filter);
  });

  @ViewChild('scrollContainer') scrollContainer!: ElementRef<HTMLDivElement>;
  private scrollTimer: ReturnType<typeof setTimeout> | null = null;

  private scrollEffect = effect(() => {
    if (this.scrollTimer) {
      clearTimeout(this.scrollTimer);
      this.scrollTimer = null;
    }

    this.filteredLines();
    if (this.autoScroll()) {
      this.scrollTimer = setTimeout(() => {
        const el = this.scrollContainer?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
        this.scrollTimer = null;
      }, 0);
    }
  });

  ngOnDestroy() {
    this.scrollEffect.destroy();
    if (this.scrollTimer) clearTimeout(this.scrollTimer);
  }

  formatTime(dateStr: string): string {
    const d = new Date(dateStr);
    return d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  readonly copied = signal(false);
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

  copyOutput() {
    const text = this.filteredLines().map(l => l.text).join('\n');
    navigator.clipboard.writeText(text).then(() => {
      this.copied.set(true);
      if (this.copiedTimer) clearTimeout(this.copiedTimer);
      this.copiedTimer = setTimeout(() => this.copied.set(false), 2000);
    });
  }

  clear() {
    // Can't clear input, but we can filter to show nothing useful
    this.filterStream.set('all');
  }
}
