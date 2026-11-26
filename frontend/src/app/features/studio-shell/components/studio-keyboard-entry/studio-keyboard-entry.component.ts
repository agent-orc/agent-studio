import { ChangeDetectionStrategy, Component, HostListener, input, output } from '@angular/core';
import { scrollMainOnKey } from './main-scroll-keys';

/**
 * AGT-3833: the shell's keyboard entry points. The skip links are the first
 * Tab stops of the page (visible only while focused), and Ctrl+K / Cmd+K is
 * owned here rather than by the search palette: the palette loads on first
 * use, so a listener inside it did not exist until search had been opened
 * once with the mouse.
 */
@Component({
  selector: 'app-studio-keyboard-entry',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-keyboard-entry.component.html',
  styleUrl: './studio-keyboard-entry.component.scss',
})
export class StudioKeyboardEntryComponent {
  /** The main content region the first skip link moves focus to. */
  readonly main = input.required<HTMLElement>();
  /** Whether this session shows Needs you at all. */
  readonly needsYouAvailable = input(false);
  /** Ctrl+K or Cmd+K was pressed anywhere on the page. */
  readonly searchShortcut = output<void>();

  @HostListener('document:keydown', ['$event'])
  onDocumentKeydown(event: KeyboardEvent): void {
    // Vertical viewport keys scroll the main region when nothing else owns them.
    // HostListener runs after the view shortcuts bubbled, so a view that took the key wins.
    if (scrollMainOnKey(event, this.main())) return;
    if (!(event.ctrlKey || event.metaKey) || event.altKey || event.key?.toLowerCase() !== 'k') return;
    event.preventDefault();
    this.searchShortcut.emit();
  }

  skipTo(event: Event, target: HTMLElement | null): void {
    event.preventDefault();
    target?.focus();
  }

  /** Needs you renders inside a deferred header control, so it is looked up when used. */
  skipToNeedsYou(event: Event): void {
    this.skipTo(event, document.querySelector<HTMLElement>('[data-testid="studio-attention-trigger"]'));
  }
}
