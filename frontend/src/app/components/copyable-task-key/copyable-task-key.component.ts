import { ChangeDetectionStrategy, Component, OnDestroy, input, signal } from '@angular/core';
import { copyTextToClipboard } from '../../services/clipboard.util';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';

export type CopyableTaskKeyVariant = 'card' | 'detail' | 'reference';

@Component({
  selector: 'app-copyable-task-key',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './copyable-task-key.component.html',
  styleUrl: './copyable-task-key.component.scss',
})
export class CopyableTaskKeyComponent implements OnDestroy {
  readonly key = input<string | null | undefined>(null);
  readonly label = input('');
  readonly variant = input<CopyableTaskKeyVariant>('card');
  readonly testId = input('copyable-task-key');
  readonly ariaLabel = input('Copy task key');
  readonly copied = signal(false);
  private resetTimer: ReturnType<typeof setTimeout> | null = null;

  async copy(event: MouseEvent): Promise<void> {
    event.stopPropagation();
    const key = this.key();
    if (!key || !await copyTextToClipboard(key)) return;
    this.copied.set(true);
    if (this.resetTimer) clearTimeout(this.resetTimer);
    this.resetTimer = setTimeout(() => this.copied.set(false), 2_000);
  }

  ngOnDestroy(): void {
    if (this.resetTimer) clearTimeout(this.resetTimer);
  }
}
