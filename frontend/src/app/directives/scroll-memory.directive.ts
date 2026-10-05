import { Directive, ElementRef, Injectable, NgZone, OnDestroy, afterNextRender, inject, input } from '@angular/core';

/** Last scroll offsets per memory key, for the lifetime of the page. */
@Injectable({ providedIn: 'root' })
export class ScrollMemoryService {
  private readonly offsets = new Map<string, { left: number; top: number }>();

  read(key: string): { left: number; top: number } | undefined {
    return this.offsets.get(key);
  }

  write(key: string, left: number, top: number): void {
    if (left === 0 && top === 0) this.offsets.delete(key);
    else this.offsets.set(key, { left, top });
  }
}

/**
 * Keeps a scroll container's offset across a destroy/re-create, such as the
 * board leaving the view while a task tab is active (AGT-2956). The board's
 * data stays resident in `TaskService`; this restores where the operator was
 * looking when it re-renders from that data. Offsets are recorded on scroll,
 * so no measurement happens during teardown.
 */
@Directive({
  selector: '[appScrollMemory]',
  standalone: true,
})
export class ScrollMemoryDirective implements OnDestroy {
  readonly appScrollMemory = input.required<string>();

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly memory = inject(ScrollMemoryService);
  private readonly zone = inject(NgZone);
  private readonly onScroll = () => {
    const el = this.host.nativeElement;
    this.memory.write(this.appScrollMemory(), el.scrollLeft, el.scrollTop);
  };

  constructor() {
    this.zone.runOutsideAngular(() =>
      this.host.nativeElement.addEventListener('scroll', this.onScroll, { passive: true }));
    afterNextRender(() => {
      const saved = this.memory.read(this.appScrollMemory());
      if (saved) this.restore(saved, true);
    });
  }

  ngOnDestroy(): void {
    this.host.nativeElement.removeEventListener('scroll', this.onScroll);
  }

  private restore(saved: { left: number; top: number }, retry: boolean): void {
    const el = this.host.nativeElement;
    el.scrollLeft = saved.left;
    el.scrollTop = saved.top;
    // Lane content can land one frame after the container; try once more
    // with the recorded offset (the clamped scroll event may have replaced it).
    const short = el.scrollLeft !== saved.left || el.scrollTop !== saved.top;
    if (retry && short && typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(() => this.restore(saved, false));
    }
  }
}
