import { Directive, ElementRef, HostBinding, HostListener, OnInit, inject, input } from '@angular/core';

export type SplitterSide = 'start' | 'end';

/** Shared persisted, pointer and keyboard-operable vertical pane splitter. */
@Directive({
  selector: '[appResizableSplitter]',
  standalone: true,
})
export class ResizableSplitterDirective implements OnInit {
  private readonly host = inject(ElementRef<HTMLElement>);
  readonly splitterStorageKey = input.required<string>();
  readonly splitterCssProperty = input.required<string>();
  readonly splitterDefaultSize = input(300);
  readonly splitterMinSize = input(200);
  readonly splitterMinOppositeSize = input(320);
  readonly splitterSide = input<SplitterSide>('start');
  readonly splitterStep = input(16);

  @HostBinding('attr.role') readonly role = 'separator';
  @HostBinding('attr.aria-orientation') readonly orientation = 'vertical';
  @HostBinding('attr.tabindex') readonly tabIndex = 0;
  @HostBinding('attr.aria-valuemin') get ariaMin(): number { return Math.round(this.minimum()); }
  @HostBinding('attr.aria-valuemax') get ariaMax(): number { return Math.round(this.maximum()); }
  @HostBinding('attr.aria-valuenow') get ariaNow(): number { return Math.round(this.size); }

  private size = 0;
  private drag: { pointerId: number } | null = null;

  ngOnInit(): void {
    this.setSize(this.readSize(), false);
  }

  @HostListener('pointerdown', ['$event'])
  onPointerDown(event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    this.drag = { pointerId: event.pointerId };
    this.host.nativeElement.setPointerCapture(event.pointerId);
    this.host.nativeElement.classList.add('is-resizing');
    document.body.style.cursor = 'col-resize';
  }

  @HostListener('pointermove', ['$event'])
  onPointerMove(event: PointerEvent): void {
    if (!this.drag || this.drag.pointerId !== event.pointerId) return;
    const rect = this.container().getBoundingClientRect();
    const raw = this.splitterSide() === 'start' ? event.clientX - rect.left : rect.right - event.clientX;
    this.setSize(raw, false);
  }

  @HostListener('pointerup', ['$event'])
  @HostListener('pointercancel', ['$event'])
  onPointerEnd(event: PointerEvent): void {
    if (!this.drag || this.drag.pointerId !== event.pointerId) return;
    this.host.nativeElement.releasePointerCapture(event.pointerId);
    this.drag = null;
    this.host.nativeElement.classList.remove('is-resizing');
    document.body.style.cursor = '';
    this.persist();
  }

  @HostListener('keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const direction = this.splitterSide() === 'start' ? 1 : -1;
    if (event.key === 'ArrowLeft') this.setSize(this.size - this.splitterStep() * direction);
    else if (event.key === 'ArrowRight') this.setSize(this.size + this.splitterStep() * direction);
    else if (event.key === 'Home') this.setSize(this.minimum());
    else if (event.key === 'End') this.setSize(this.maximum());
    else return;
    event.preventDefault();
  }

  @HostListener('dblclick', ['$event'])
  onDoubleClick(event: MouseEvent): void {
    event.preventDefault();
    this.setSize(this.splitterDefaultSize());
  }

  private setSize(raw: number, persist = true): void {
    this.size = Math.round(Math.max(this.minimum(), Math.min(this.maximum(), raw)));
    this.container().style.setProperty(this.splitterCssProperty(), `${this.size}px`);
    if (persist) this.persist();
  }

  private minimum(): number {
    const width = this.container().getBoundingClientRect().width;
    return width > 0 ? Math.min(this.splitterMinSize(), width * 0.39) : this.splitterMinSize();
  }

  private maximum(): number {
    const width = this.container().getBoundingClientRect().width;
    if (width <= 0) return Number.MAX_SAFE_INTEGER;
    const opposite = Math.min(this.splitterMinOppositeSize(), width * 0.6);
    return Math.max(this.minimum(), width - 1 - opposite);
  }

  private container(): HTMLElement {
    return this.host.nativeElement.parentElement as HTMLElement;
  }

  private readSize(): number {
    try {
      const value = Number(localStorage.getItem(this.splitterStorageKey()));
      if (Number.isFinite(value) && value > 0) return value;
    } catch { /* storage can be unavailable in privacy mode */ }
    return this.splitterDefaultSize();
  }

  private persist(): void {
    try { localStorage.setItem(this.splitterStorageKey(), String(this.size)); }
    catch { /* storage can be unavailable in privacy mode */ }
  }
}
