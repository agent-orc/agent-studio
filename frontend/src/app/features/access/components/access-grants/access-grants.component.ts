import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, effect, inject, input, signal, viewChild } from '@angular/core';
import { ChipComponent } from '../../../../components/chip/chip.component';
import type { GrantView } from '../../models/access-view.model';

/**
 * Role bindings as chips: the role and, in the second segment, where it
 * applies. In a grid (`fit`) the chips stay on one line: as many as fit are
 * shown and the rest is counted, so every row keeps its height. The detail
 * shows all of them, with the group a role comes through.
 */
@Component({
  selector: 'app-access-grants',
  standalone: true,
  imports: [ChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-grants.component.html',
  styleUrl: './access-grants.component.scss',
})
export class AccessGrantsComponent {
  readonly grants = input.required<readonly GrantView[]>();
  /** One line: show the chips that fit and count the rest. */
  readonly fit = input(false);
  readonly emptyLabel = input('No role');

  /** Chips that wrapped out of the one visible line. */
  readonly more = signal(0);
  private readonly list = viewChild<ElementRef<HTMLElement>>('list');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private observer: ResizeObserver | null = null;

  constructor() {
    effect(() => {
      this.grants();
      this.fit();
      queueMicrotask(() => this.measure());
    });
    afterNextRender(() => {
      const list = this.list()?.nativeElement;
      if (!list || typeof ResizeObserver === 'undefined') return;
      // The host keeps the width of the cell; the list itself is narrowed by measure().
      this.observer = new ResizeObserver(() => this.measure());
      this.observer.observe(this.host.nativeElement);
    });
    inject(DestroyRef).onDestroy(() => this.observer?.disconnect());
  }

  private measure(): void {
    const list = this.list()?.nativeElement;
    if (!list) return;
    // Measure at the full width, then shrink the list to its visible chips so the count sits right after them.
    list.style.width = '';
    // Nothing is cut off unless the list is held to one line and its content is taller than that.
    if (!this.fit() || list.scrollHeight <= list.clientHeight + 1) {
      this.more.set(0);
      return;
    }
    const chips = Array.from(list.children) as HTMLElement[];
    const firstLine = chips[0]?.offsetTop ?? 0;
    const visible = chips.filter(chip => chip.offsetTop <= firstLine);
    const last = visible.at(-1);
    // Rounded up: a list a fraction of a pixel too narrow would cut the last chip off.
    if (last) list.style.width = `${Math.ceil(last.getBoundingClientRect().right - list.getBoundingClientRect().left) + 1}px`;
    this.more.set(chips.length - visible.length);
  }
}
