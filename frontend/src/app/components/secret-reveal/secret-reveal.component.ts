import { ChangeDetectionStrategy, Component, OnDestroy, input, output, signal } from '@angular/core';

/**
 * The one place a secret is shown: a new token, an enrollment code, a
 * one-time link or password. It is marked as shown once, carries its own
 * copy control, and stays until the reader dismisses it.
 *
 *   <app-secret-reveal title="New token for deploy-bot" [value]="token"
 *                      note="It is not shown again. Expires 22 Feb 2027."
 *                      copyLabel="Copy token" (dismissed)="issued.set(null)" />
 *
 * Projected content (host commands, a follow-up step) renders under the value.
 */
@Component({
  selector: 'app-secret-reveal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './secret-reveal.component.html',
  styleUrl: './secret-reveal.component.scss',
})
export class SecretRevealComponent implements OnDestroy {
  readonly title = input.required<string>();
  readonly value = input.required<string>();
  /** What the reader must know: that it is shown once, and when it expires. */
  readonly note = input<string | null>(null);
  readonly copyLabel = input('Copy');
  readonly testId = input('secret-reveal');

  readonly dismissed = output<void>();

  readonly copied = signal(false);
  readonly copyFailed = signal(false);
  private reset: ReturnType<typeof setTimeout> | null = null;

  async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.value());
      this.copied.set(true);
      this.copyFailed.set(false);
      if (this.reset) clearTimeout(this.reset);
      this.reset = setTimeout(() => this.copied.set(false), 2000);
    } catch {
      this.copyFailed.set(true);
    }
  }

  ngOnDestroy(): void {
    if (this.reset) clearTimeout(this.reset);
  }
}
