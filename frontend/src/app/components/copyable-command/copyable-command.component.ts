import { ChangeDetectionStrategy, Component, OnDestroy, input, signal } from '@angular/core';

/**
 * AGT-3580 - a shell command behind a remedy, shown as a detail the operator
 * can copy rather than as the remedy itself. Used where the fix has no UI
 * equivalent (the remedy prose says what to do; this says how, on the host).
 */
@Component({
  selector: 'app-copyable-command',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './copyable-command.component.html',
  styleUrl: './copyable-command.component.scss',
})
export class CopyableCommandComponent implements OnDestroy {
  readonly command = input.required<string>();
  readonly testId = input('copyable-command');
  readonly copied = signal(false);
  private reset: ReturnType<typeof setTimeout> | null = null;

  async copy(): Promise<void> {
    try {
      await navigator.clipboard?.writeText(this.command());
      this.copied.set(true);
      if (this.reset) clearTimeout(this.reset);
      this.reset = setTimeout(() => this.copied.set(false), 1500);
    } catch {
      /* Clipboard access is best-effort; the text stays selectable. */
    }
  }

  ngOnDestroy(): void {
    if (this.reset) clearTimeout(this.reset);
  }
}
