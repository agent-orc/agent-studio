import { ChangeDetectionStrategy, Component, ViewEncapsulation, computed, input, output } from '@angular/core';

/**
 * Unified notification / banner primitive (F37). Owns the shared layout
 * (icon + body + optional close), severity tinting via Tier-2 design
 * tokens, and two thickness presets ("toast" with floating elevation;
 * "banner" with flat in-flow chrome).
 *
 * Existing surfaces consume this component via slot projection:
 *   - `<app-notification-stack>` mounts one per active toast;
 *   - persistent workspace alarms use `layout="banner"` for the shared,
 *     full-bleed notice-bar treatment.
 *
 * The component is intentionally agent-/app-agnostic. It does not know
 * about the notification service, the message bus, or any business
 * concept; callers translate their state into `(kind, icon, body,
 * title)` and the component renders. Light + dark themes flip
 * automatically because every colour comes from the
 * `--notify-*` tokens declared in `_tokens-semantic.scss`.
 */
export type NotificationKind = 'success' | 'info' | 'warning' | 'error' | 'accent';
export type NotificationLayout = 'toast' | 'banner';

const DEFAULT_ICON: Record<NotificationKind, string> = {
  success: '✓',  // ✓
  info: 'ℹ',     // ℹ
  warning: '⚠',  // ⚠
  error: '⚠',    // ⚠ (caller may override with ✕)
  accent: '🤖', // 🤖 — orchestrator-flavoured default
};

@Component({
  selector: 'sui-notification, app-notification',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './notification.component.html',
  styleUrl: './notification.component.scss',
})
export class NotificationComponent {
  /** Severity / colour variant. Default 'info'. */
  readonly kind = input<NotificationKind>('info');

  /** Visual density. Toasts float (popover elevation); banners are flat in-flow. */
  readonly layout = input<NotificationLayout>('toast');

  /** Override the default glyph for the chosen `kind`. */
  readonly icon = input<string | null>(null);

  /** Optional bold heading rendered above the body. */
  readonly title = input<string | null>(null);

  /**
   * When `true`, render the trailing close button. The component does
   * not own dismissal state; the host listens to `(closeRequest)` and
   * unmounts / hides accordingly.
   */
  readonly closable = input(false);

  /** ARIA live region politeness. Errors / warnings default to assertive. */
  readonly ariaLive = input<'polite' | 'assertive' | null>(null);

  /** Pass-through data-testid for stable selection in Playwright. */
  readonly testid = input<string | null>(null);

  readonly closeRequest = output<void>();

  readonly resolvedIcon = computed(() => this.icon() ?? DEFAULT_ICON[this.kind()]);

  readonly resolvedAriaLive = computed<'polite' | 'assertive'>(() => {
    const explicit = this.ariaLive();
    if (explicit) return explicit;
    const k = this.kind();
    return k === 'error' || k === 'warning' ? 'assertive' : 'polite';
  });
}
