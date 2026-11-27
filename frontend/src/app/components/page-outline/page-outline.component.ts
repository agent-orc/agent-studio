import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { CountBadgeComponent } from '@studio/ui/components/count-badge/count-badge.component';

export interface OutlineItem {
  /** Element id of the section, the `anchor` of its `<app-page-section>`. */
  id: string;
  label: string;
  /** Number of entries in the section; `null` shows none. */
  count?: number | null;
  /** Heading of the group this item starts; items after it belong to the same group. */
  group?: string;
}

/**
 * The outline of a long page: one entry per section, with its count, that
 * jumps to the section and marks the one in view. The active entry reads the
 * shared navigation tokens (`--studio-nav-active-*`).
 *
 *   <app-page-outline label="On this page" [items]="outline()" />
 *
 * `orientation="horizontal"` renders one scrolling line for narrow layouts
 * and drops the group headings.
 */
@Component({
  selector: 'app-page-outline',
  standalone: true,
  imports: [CountBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './page-outline.component.html',
  styleUrl: './page-outline.component.scss',
})
export class PageOutlineComponent {
  readonly items = input.required<readonly OutlineItem[]>();
  readonly label = input('On this page');
  readonly orientation = input<'vertical' | 'horizontal'>('vertical');
  readonly testId = input('page-outline');

  readonly active = signal<string | null>(null);
  /** Set by a jump: the chosen entry stays marked until the reader scrolls on their own. */
  private pinned: string | null = null;
  private frame = 0;
  private readonly onScroll = () => this.schedule();
  private readonly onUserScroll = () => { this.pinned = null; };

  constructor() {
    // Sections can appear after a load, so the current one is recomputed whenever the entries change.
    effect(() => {
      this.items();
      this.schedule();
    });
    if (typeof document === 'undefined') return;
    // Capture: a scroll event does not bubble out of the element that scrolls.
    document.addEventListener('scroll', this.onScroll, { capture: true, passive: true });
    window.addEventListener('resize', this.onScroll, { passive: true });
    for (const type of ['wheel', 'touchmove', 'keydown'] as const) document.addEventListener(type, this.onUserScroll, { capture: true, passive: true });
    inject(DestroyRef).onDestroy(() => {
      document.removeEventListener('scroll', this.onScroll, { capture: true });
      window.removeEventListener('resize', this.onScroll);
      for (const type of ['wheel', 'touchmove', 'keydown'] as const) document.removeEventListener(type, this.onUserScroll, { capture: true });
      if (this.frame) cancelAnimationFrame(this.frame);
    });
  }

  jump(id: string): void {
    const target = typeof document === 'undefined' ? null : document.getElementById(id);
    if (!target) return;
    this.pinned = id;
    this.active.set(id);
    const reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    target.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth', block: 'start' });
    document.getElementById(`${id}-title`)?.focus({ preventScroll: true });
  }

  private schedule(): void {
    if (this.frame || typeof requestAnimationFrame === 'undefined') return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.active.set(this.pinned ?? this.current());
    });
  }

  /** The last section whose top has passed the reading line, a third of the way down the scrolling area. */
  private current(): string | null {
    const sections = this.items().map(item => document.getElementById(item.id)).filter((element): element is HTMLElement => !!element);
    if (sections.length === 0) return null;
    const scroller = scrollParent(sections[0]);
    // At the top of the page the first section is the current one, whatever the reading line crosses.
    if (!scroller || scroller.scrollTop <= 0) return sections[0].id;
    if (scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 1) return sections.at(-1)!.id;
    const line = scroller.getBoundingClientRect().top + scroller.clientHeight / 3;
    let current = sections[0];
    for (const section of sections) if (section.getBoundingClientRect().top <= line) current = section;
    return current.id;
  }
}

function scrollParent(element: HTMLElement): HTMLElement | null {
  for (let node = element.parentElement; node; node = node.parentElement) {
    const overflow = getComputedStyle(node).overflowY;
    if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) return node;
  }
  return null;
}
