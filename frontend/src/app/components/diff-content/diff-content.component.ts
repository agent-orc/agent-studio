import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { DomSanitizer, type SafeHtml } from '@angular/platform-browser';
import { currentDiff2Html, hasDiff2HtmlLoaded, loadDiff2Html } from '../../utils/diff2html-lazy';
import { StatusIndicatorComponent } from '../status-indicator/status-indicator.component';

/**
 * Shared unified-diff renderer. The single place a diff string is turned into
 * `diff2html` HTML, so every diff surface (the full-screen Studio diff tab, the
 * per-task Git pane, the Project Hub Git View) renders identical output and the
 * heavy `diff2html` chunk is lazy-loaded exactly once. Callers own the gating
 * (large-diff reveal, loading/error/empty states) and pass a ready-to-render
 * diff string via {@link diffText}; this component only renders. It intentionally
 * does no HTTP and holds no domain state.
 *
 * The rendered HTML is inserted via `[innerHTML]`, so the `.d2h-*` theme rules
 * live here (scoped to the render container with `::ng-deep`) instead of being
 * re-declared by every consumer.
 */
@Component({
  selector: 'app-diff-content',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './diff-content.component.html',
  styleUrl: './diff-content.component.scss',
})
export class DiffContentComponent {
  private readonly sanitizer = inject(DomSanitizer);

  /** The unified-diff text to render. Empty renders nothing. */
  readonly diffText = input.required<string>();
  /** Side-by-side (true) vs line-by-line (false) layout. */
  readonly sideBySide = input(true);

  /** Whether the lazy diff2html module has finished importing. */
  private readonly ready = signal(hasDiff2HtmlLoaded());

  // Trigger the dynamic import the first time we're handed a non-empty diff.
  // Until it resolves, html() returns null and the template shows a small
  // placeholder; the moment the import lands the signal flips and the computed
  // re-runs synchronously (the module is cached for every later render).
  private readonly _ensureLoaded = effect(() => {
    if (!this.diffText()) return;
    if (this.ready()) return;
    loadDiff2Html().then(() => this.ready.set(true));
  });

  readonly html = computed<SafeHtml | null>(() => {
    const text = this.diffText();
    if (!text) return null;
    const mod = currentDiff2Html();
    if (!this.ready() || !mod) return null;
    const rendered = mod.html(text, {
      drawFileList: false,
      outputFormat: this.sideBySide() ? 'side-by-side' : 'line-by-line',
      matching: 'lines',
      colorScheme: mod.darkScheme,
    });
    return this.sanitizer.bypassSecurityTrustHtml(rendered);
  });
}
