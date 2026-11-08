import {
  AfterViewInit,
  DestroyRef,
  Directive,
  ElementRef,
  effect,
  inject,
  input,
} from '@angular/core';
import {
  resolveTaskArtifactLink,
  type TaskArtifactLinkContext,
} from './task-artifact-link';

const SOURCE_HREF_ATTR = 'data-task-artifact-source-href';

/**
 * Host-side wiring for the canonical `cac-markdown` renderer.
 *
 * The chat library deliberately has no knowledge of Studio task folders. This
 * directive observes rendered anchors below a task surface and rewrites only
 * guarded `results/` and `logs/` references with the current card context. It
 * lives on the surface root so it also covers markdown rendered inside the
 * library-owned conversation component.
 */
@Directive({
  selector: '[appTaskArtifactLinks]',
  standalone: true,
})
export class TaskArtifactLinksDirective implements AfterViewInit {
  readonly appTaskArtifactLinks = input<TaskArtifactLinkContext | null>(null);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private observer: MutationObserver | null = null;

  constructor() {
    effect(() => {
      this.appTaskArtifactLinks();
      queueMicrotask(() => this.rewriteLinks());
    });
  }

  ngAfterViewInit(): void {
    this.rewriteLinks();
    if (typeof MutationObserver === 'undefined') return;
    this.observer = new MutationObserver(() => this.rewriteLinks());
    this.observer.observe(this.host.nativeElement, { childList: true, subtree: true });
    this.destroyRef.onDestroy(() => this.observer?.disconnect());
  }

  private rewriteLinks(): void {
    // Markdown renderers emit disabled task-list checkboxes beside their text.
    // Give each control the adjacent item's name when the rendered DOM changes.
    for (const checkbox of this.host.nativeElement.querySelectorAll<HTMLInputElement>(
      'li input[type="checkbox"][disabled]:not([aria-label])',
    )) {
      const label = checkbox.closest('li')?.textContent?.replace(/\s+/g, ' ').trim();
      checkbox.setAttribute('aria-label', label || 'Checklist item');
    }

    const context = this.appTaskArtifactLinks();
    if (!context) return;

    for (const anchor of this.host.nativeElement.querySelectorAll<HTMLAnchorElement>('a[href]')) {
      const sourceHref = anchor.getAttribute(SOURCE_HREF_ATTR) ?? anchor.getAttribute('href');
      const resolved = resolveTaskArtifactLink(sourceHref, context);
      if (!resolved) continue;

      anchor.setAttribute(SOURCE_HREF_ATTR, sourceHref ?? '');
      anchor.setAttribute('href', resolved.href);
      anchor.setAttribute('target', '_blank');
      anchor.setAttribute('rel', 'noopener noreferrer');
      anchor.setAttribute('data-task-artifact-link', resolved.relativePath);
    }
  }
}
