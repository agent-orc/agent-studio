# Public API

Generated TypeScript declarations for the source API and its public dependencies. Regenerate with `node scripts/check-ui-library.mjs --write-api`.

## components/app-dialog/confirm-dialog/confirm-dialog.component.d.ts

```ts
/**
 * App-wide confirm dialog. Mounted once at the shell; visibility driven by
 * `ConfirmDialogService.active()`. Renders a modal with the same panel,
 * eyebrow, typography, and footer pattern as the error overlay so every
 * confirmation feels native to the dark Catppuccin-inspired UI.
 *
 * Behaviour:
 *  - Esc cancels.
 *  - Enter confirms (primary action).
 *  - Backdrop click cancels.
 *  - Focus is moved to the primary button when the dialog opens and the
 *    Tab key is trapped between the two action buttons + close affordance.
 *
 * Errors with stack-trace / copy remain on `ErrorDialogService` — those
 * carry strictly more information and rely on the dedicated overlay.
 */
export declare class ConfirmDialogComponent {
    private readonly service;
    private readonly modalStack;
    private readonly destroyRef;
    readonly state: import("@angular/core").WritableSignal<import("../../../models/app-dialog.model").ConfirmDialogState | null>;
    readonly hasOpen: import("@angular/core").Signal<boolean>;
    /** Live value of the type-to-confirm input (empty when not required). */
    readonly typedDraft: import("@angular/core").WritableSignal<string>;
    /**
     * True when the dialog has no type-to-confirm gate, or the trimmed input
     * matches one of the required values (case-insensitive). Drives both the
     * confirm button's disabled state and the Enter-to-confirm guard so a
     * destructive delete cannot complete until the operator re-types the
     * project name or short code.
     */
    readonly typedConfirmSatisfied: import("@angular/core").Signal<boolean>;
    /** Live value of the written-reason input (empty when not required). */
    readonly reasonDraft: import("@angular/core").WritableSignal<string>;
    /** True when no reason is required, or the trimmed reason is long enough. */
    readonly reasonSatisfied: import("@angular/core").Signal<boolean>;
    /** Both confirmation gates; drives the confirm button and Enter. */
    readonly canConfirm: import("@angular/core").Signal<boolean>;
    private confirmBtnRef?;
    private typedInputRef?;
    private reasonInputRef?;
    /** Tracks the previously-focused element so we can restore focus on close. */
    private previousFocus;
    private modalStackDispose;
    constructor();
    onAccept(): void;
    onCancel(): void;
    onBackdropClick(): void;
    /** R8: the item list owns its vertical scroll keys while it has focus. */
    containListKeys(event: KeyboardEvent): void;
    /**
     * Enter confirms when the focus is on the dialog. We avoid hijacking
     * Enter globally — the global handler is scoped to keydown originating
     * inside the dialog's panel via the template.
     */
    onPanelKeydown(event: KeyboardEvent): void;
}
```

## components/app-dialog/notification-stack/notification-stack.component.d.ts

```ts
import { NotificationAction, NotificationState } from '../../../models/app-dialog.model';
/**
 * Stack of persistent notification toasts (success / info / warning / error).
 * Mounted once at the app shell; visibility driven by
 * `NotificationService.notifications()`.
 *
 * F56: all notifications render as toasts in a top-right stack. Click-to-dismiss
 * is the default; auto-dismiss only for short success/info without actions.
 * Escape dismisses the topmost toast (when no modal has consumed the event).
 *
 * Toasts are routed into two positioned containers by their `position`
 * field: the default `top-right` pile and a `bottom-right` pile reserved
 * for action toasts (Move/Undo) that must not occlude the top-right
 * context menu. A third pile docks `below-toolbar` toasts under the task
 * tab toolbar and the card's refusal banner (AGT-3873), so a refusal toast
 * never covers the toolbar action it names.
 */
export declare class NotificationStackComponent {
    private readonly service;
    readonly notifications: import("@angular/core").WritableSignal<NotificationState[]>;
    /** Default pile (top-right): everything without an explicit bottom-right. */
    readonly topRight: import("@angular/core").Signal<NotificationState[]>;
    /** Bottom-right pile: action toasts that would otherwise cover top-right UI. */
    readonly bottomRight: import("@angular/core").Signal<NotificationState[]>;
    /** Below-toolbar pile: refusal toasts whose action lives in the task toolbar. */
    readonly belowToolbar: import("@angular/core").Signal<NotificationState[]>;
    /** Viewport top of the below-toolbar pile in px; null keeps the stylesheet default. */
    readonly belowToolbarTop: import("@angular/core").WritableSignal<number | null>;
    constructor();
    private measureToolbarDock;
    dismiss(id: number): void;
    onAction(action: NotificationAction, toastId: number): void;
}
/**
 * The viewport y below which a below-toolbar toast may sit: under the task
 * tab bar (`studio-tabbar`, which carries the toolbar), and under the card's
 * refusal banner and the open override form while they are on screen. Null
 * when no tab bar renders.
 */
export declare function toolbarDockTop(root: ParentNode, viewportHeight: number): number | null;
```

## components/async-feedback/loading-surface/loading-surface.component.d.ts

```ts
export type LoadingSurfaceKind = 'board' | 'task-detail' | 'list';
/** Skeleton feedback that stays hidden for fast (<200 ms) loads. */
export declare class LoadingSurfaceComponent {
    readonly kind: import("@angular/core").InputSignal<LoadingSurfaceKind>;
    readonly label: import("@angular/core").InputSignal<string>;
    readonly visible: import("@angular/core").WritableSignal<boolean>;
    readonly contextual: import("@angular/core").WritableSignal<boolean>;
    constructor();
}
```

## components/async-feedback/pending-button.directive.d.ts

```ts
/**
 * Shared pending-state contract for native buttons. Consumers keep their
 * existing button styling and content while this directive adds the common
 * disabled, busy, spinner and pending-label behaviour.
 *
 * The directive owns the host `disabled` property: a `[disabled]` binding on
 * the button feeds the `disabled` input below, and the button is disabled
 * while pending or while that binding says so. AGT-3700: an earlier version
 * snapshotted the DOM state when pending began and restored it afterwards,
 * which left a button disabled for good when its own binding flipped during
 * the pending work (a `canSubmit` that includes the busy flag).
 */
export declare class PendingButtonDirective {
    readonly pending: import("@angular/core").InputSignal<boolean>;
    readonly pendingLabel: import("@angular/core").InputSignal<string>;
    readonly disabled: import("@angular/core").InputSignalWithTransform<boolean, unknown>;
    get isDisabled(): boolean;
    get pendingClass(): boolean;
    get ariaBusy(): string | null;
    get label(): string | null;
}
```

## components/copyable-command/copyable-command.component.d.ts

```ts
import { OnDestroy } from '@angular/core';
/**
 * AGT-3580 - a shell command behind a remedy, shown as a detail the operator
 * can copy rather than as the remedy itself. Used where the fix has no UI
 * equivalent (the remedy prose says what to do; this says how, on the host).
 * With a `label` and `location` it shows a configuration entry instead (the
 * file and key it goes under), so text for a file never reads as a command.
 */
export declare class CopyableCommandComponent implements OnDestroy {
    readonly command: import("@angular/core").InputSignal<string>;
    readonly testId: import("@angular/core").InputSignal<string>;
    /** What the text is: `Command` by default, `Config entry` for text that goes into a file. */
    readonly label: import("@angular/core").InputSignal<string>;
    /** Where the text goes, for a configuration entry (file and key path). */
    readonly location: import("@angular/core").InputSignal<string | null>;
    readonly copied: import("@angular/core").WritableSignal<boolean>;
    private reset;
    copy(): Promise<void>;
    ngOnDestroy(): void;
}
```

## components/count-badge/count-badge.component.d.ts

```ts
/**
 * The single canonical count pill used across the studio sidebar — panel
 * section headers (Workspaces, Open tabs), tree rows (project totals,
 * workspace project counts) and the CLI agent rows. Before this existed
 * the same badge was hand-rolled four times with drifting padding /
 * radius / colour (`section-header__count`, `studio-explorer__count`,
 * `tree-row__count`, `studio-cli__count`); ASS-707 (count-badge padding)
 * now lives in one place so every count reads identically.
 *
 *   <app-count-badge [value]="42" />
 *
 * Pass `tone="active"` when the row owning the badge is selected so the
 * pill inverts to the accent fill instead of the muted elevated chip.
 * Pass `variant="pane-tab"` for tab-strip metadata counts, where the badge
 * needs to stay calmer and align with the tab icon/label row.
 */
export declare class CountBadgeComponent {
    /** The number/label to show. `null` renders nothing. */
    readonly value: import("@angular/core").InputSignal<string | number | null>;
    /** `active` inverts the pill to the accent fill for selected rows. */
    readonly tone: import("@angular/core").InputSignal<"default" | "active">;
    /** Context variant. Defaults preserve the canonical sidebar/tree pill. */
    readonly variant: import("@angular/core").InputSignal<"default" | "pane-tab">;
}
```

## components/dialog/dialog.component.d.ts

```ts
import { OnDestroy, OnInit } from '@angular/core';
/**
 * Reusable modal dialog skeleton.
 *
 * The companion to <app-sidesheet>: this owns the **modal** shape
 * (centred panel, backdrop click-to-close, alertdialog ARIA, Esc
 * registration with ModalStackService, Tab containment and focus return
 * to the opener through `ModalFocusDirective`). Owners that already register
 * an Escape handler set `stackManagedExternally` to avoid a duplicate entry.
 * The two shapes are
 * deliberately separate components — they look similar but their
 * interaction semantics differ.
 *
 * Use this for: error, confirm, create-job, media-lightbox,
 * e2e-cleanup, update-block, update-center, verbose-debug,
 * orchestrator-settings — anything that asks the user to make a
 * one-off decision against the rest of the app.
 *
 * **Not** for side panels — those use <app-sidesheet>.
 *
 * Usage:
 *
 *   <app-dialog
 *     eyebrow="Confirm delete"
 *     title="Delete this task?"
 *     role="alertdialog"
 *     (close)="onCancel()"
 *     (backdropClick)="onCancel()">
 *     <p>This removes the job folder and all its files.</p>
 *     <div footer class="my-actions">
 *       <button (click)="onCancel()">Cancel</button>
 *       <button (click)="onConfirm()">Delete</button>
 *     </div>
 *   </app-dialog>
 *
 * Backdrop click is opt-in via the `(backdropClick)` event so callers
 * keep full control over cancellation semantics — some dialogs
 * (verbose-debug, log overlays) intentionally don't close on a
 * mis-click.
 */
export declare class DialogComponent implements OnInit, OnDestroy {
    readonly eyebrow: import("@angular/core").InputSignal<string | null>;
    readonly title: import("@angular/core").InputSignal<string>;
    /** Optional one-line caption under the title. */
    readonly subtitle: import("@angular/core").InputSignal<string | null>;
    /** ARIA role — `alertdialog` for confirms/errors, `dialog` for forms. */
    readonly role: import("@angular/core").InputSignal<"dialog" | "alertdialog">;
    /** Optional exact width override (px). Wins over `widthTier`. Default 520. */
    readonly width: import("@angular/core").InputSignal<number | null>;
    /**
     * Responsive width tier. `md` (default) keeps the compact 520px panel for
     * confirm / error / form dialogs. `lg` switches to `--studio-modal-width-lg`
     * (`min(90vw, 880px)`) so content-heavy modals grow with the viewport and
     * shrink on small screens instead of stranding empty gutters. An explicit
     * `[width]` px override still takes precedence over the tier.
     */
    readonly widthTier: import("@angular/core").InputSignal<"md" | "lg">;
    /** Hides the close button when set to `false`. */
    readonly closable: import("@angular/core").InputSignal<boolean>;
    /** Visual variant — drives accent stripe colour. */
    readonly kind: import("@angular/core").InputSignal<"danger" | "primary" | "default">;
    /**
     * Body padding variant. `md` (default) uses --studio-modal-padding-body
     * (24px); `sm` drops to --studio-modal-padding-body-sm (16px) for confirm-
     * style dialogs that are intentionally tight. Header/footer always use
     * --studio-modal-padding-header / -footer regardless of size.
     */
    readonly size: import("@angular/core").InputSignal<"md" | "sm">;
    readonly testid: import("@angular/core").InputSignal<string | null>;
    /** Set when the owner already registers this dialog with ModalStackService. */
    readonly stackManagedExternally: import("@angular/core").InputSignal<boolean>;
    /**
     * Relocate this dialog's host element to `<body>` on init.
     *
     * The overlay is `position: fixed`, but a fixed element is positioned
     * relative to the nearest ancestor that establishes a containing block
     * for fixed descendants — `transform`, `filter`, or `contain:
     * layout/paint` all do this. When the dialog renders inline inside such
     * an ancestor (e.g. the lane `.column`, which carries `contain: layout
     * paint` for scroll perf), the overlay is positioned against that tall,
     * scrolled box and then clipped off-screen by its `overflow: hidden`
     * parents instead of covering the viewport. Hosting at `<body>` escapes
     * both the containing block and the clipping. On by default because all
     * modal dialogs belong on the central overlay layer.
     */
    readonly portalToBody: import("@angular/core").InputSignal<boolean>;
    private readonly host;
    private readonly overlayPortal;
    private readonly modalStack;
    private portalRef;
    private stackDispose;
    ngOnInit(): void;
    ngOnDestroy(): void;
    readonly closeRequest: import("@angular/core").OutputEmitterRef<void>;
    readonly backdropClick: import("@angular/core").OutputEmitterRef<void>;
    onBackdropClick(): void;
    /** Fallback for a dialog whose owner handles Escape outside the stack. */
    onEscape(): void;
}
```

## components/disclosure-marker/disclosure-marker.component.d.ts

```ts
/**
 * The one disclosure marker of the admin grammar (AGT-2813, guideline rule
 * ADM-17).
 *
 * Every element that expands content in place announces itself with this
 * marker: a leading chevron that points right while collapsed and rotates a
 * quarter turn while open. It is decoration only — the state a screen reader
 * hears is `aria-expanded` on the control that owns it, so the host element is
 * `aria-hidden`.
 *
 * Usage: place it as the FIRST child of the clickable element, never as a
 * trailing caret and never as a second control beside the label:
 *
 * ```html
 * <button type="button" class="thing__head"
 *         [attr.aria-expanded]="open()" (click)="toggle()">
 *   <app-disclosure-marker [open]="open()" />
 *   <span>Why this status?</span>
 * </button>
 * ```
 *
 * The hit area, hover wash and focus ring that complete the rule live in the
 * `disclosure-control` mixin (`styles/_mixins.scss`).
 */
export declare class DisclosureMarkerComponent {
    /** Mirrors the owning control's `aria-expanded`. */
    readonly open: import("@angular/core").InputSignal<boolean>;
    /** Glyph size in px. 12 is the default row scale; 10 suits dense rails. */
    readonly size: import("@angular/core").InputSignal<number>;
}
```

## components/empty-state/empty-state.component.d.ts

```ts
import { StudioIconName } from '../studio-icon/studio-icon.component';
/**
 * Padded, muted text used when a panel / list / section has nothing
 * to show. Centralises the "italic, muted, padded" recipe so every
 * empty state reads the same across the app.
 *
 * Two usage shapes:
 *
 *   1. Headline + body via inputs:
 *      <app-empty-state
 *        icon="bot"
 *        title="No agents have spoken yet"
 *        body="Once the orchestrator or a CLI emits to the bus,
 *              the timeline + counters fill in here." />
 *
 *   2. Single-line variant via content projection:
 *      <app-empty-state>No projects loaded.</app-empty-state>
 *
 * Reads only studio-shell tokens so it flips per theme automatically.
 */
export declare class EmptyStateComponent {
    readonly icon: import("@angular/core").InputSignal<StudioIconName | null>;
    readonly title: import("@angular/core").InputSignal<string | null>;
    readonly body: import("@angular/core").InputSignal<string | null>;
    /** Smaller padding + body-only variant (used inside dense lists). */
    readonly compact: import("@angular/core").InputSignal<boolean>;
    readonly testid: import("@angular/core").InputSignal<string | null>;
}
```

## components/error-dialog/error-dialog.component.d.ts

```ts
import { ErrorDialogState } from '../../models/error-dialog.model';
/**
 * Error overlay used by the global ErrorDialogService. The parent
 * watches `errorDialog.activeError()` and renders this component when
 * an error is present. An expected refusal (AGT-3624) renders its plain
 * sentence and next action, with the raw payload behind Technical details.
 */
export declare class ErrorDialogComponent {
    readonly error: import("@angular/core").InputSignal<ErrorDialogState>;
    readonly canOpenCliConfig: import("@angular/core").InputSignal<boolean>;
    readonly copyButtonLabel: import("@angular/core").InputSignal<string>;
    readonly closeRequest: import("@angular/core").OutputEmitterRef<void>;
    readonly copyRequest: import("@angular/core").OutputEmitterRef<void>;
    readonly openCliConfig: import("@angular/core").OutputEmitterRef<void>;
    /** Technical details of a refusal start collapsed. */
    readonly technicalOpen: import("@angular/core").WritableSignal<boolean>;
    constructor();
}
```

## components/info-button/help-loader.d.ts

```ts
import { InjectionToken } from '@angular/core';
export interface HelpDocument {
    readonly topic: string;
    readonly title: string;
    readonly body: string;
}
export declare const STUDIO_HELP_LOADER: InjectionToken<(topic: string) => Promise<HelpDocument>>;
```

## components/info-button/info-button-markdown-body/info-button-markdown-body.component.d.ts

```ts
import { InjectionToken, Type } from '@angular/core';
export declare const STUDIO_HELP_RENDERER: InjectionToken<() => Promise<Type<unknown>>>;
export declare class InfoButtonMarkdownBodyComponent {
    readonly source: import("@angular/core").InputSignal<string>;
    readonly renderer: import("@angular/core").WritableSignal<Type<unknown> | null>;
    constructor();
}
```

## components/info-button/info-button.component.d.ts

```ts
/**
 * Subtle "i" trigger that opens a centered modal with the rendered
 * concept doc for the given topic. The doc body is fetched on first open
 * from <c>GET /api/concept-docs/{topic}</c>; the FE never duplicates the
 * prose. Body is rendered through the canonical Markdown view,
 * and the modal shell is the app-wide {@link DialogComponent} so the
 * surface flips light/dark from studio tokens with no per-host colours.
 *
 * Every lane carries one: the trigger is wired from the board lane header
 * (and the studio-shell active panel) so an operator can read what any
 * lane means without leaving the surface.
 */
export declare class InfoButtonComponent {
    private readonly loadDocument;
    readonly topic: import("@angular/core").InputSignal<string>;
    /** Eyebrow shown above the modal title. Defaults to the lane framing. */
    readonly eyebrow: import("@angular/core").InputSignal<string>;
    /** Trigger aria-label + tooltip. Defaults to the lane framing. */
    readonly label: import("@angular/core").InputSignal<string>;
    /** Header title shown until the doc's own H1 loads (or on error). */
    readonly fallbackTitle: import("@angular/core").InputSignal<string>;
    readonly open: import("@angular/core").WritableSignal<boolean>;
    readonly loading: import("@angular/core").WritableSignal<boolean>;
    readonly errorMessage: import("@angular/core").WritableSignal<string | null>;
    private readonly doc;
    readonly title: import("@angular/core").Signal<string>;
    readonly body: import("@angular/core").Signal<string | null>;
    toggle(event: Event): void;
    close(): void;
    private readonly modalStack;
    private readonly destroyRef;
    private modalStackDispose;
    private readonly stackEffect;
    private readonly stackTeardown;
    private load;
}
```

## components/list-row/list-row.component.d.ts

```ts
/**
 * The single canonical flat list row for the studio sidebar panels. A row is
 * one line: an optional leading glyph/icon (`[lead]` slot), a truncating
 * label, and an optional trailing affordance (`[trail]` slot — e.g. an
 * `app-count-badge` or a close button). It replaces the per-panel row markup
 * that had drifted apart (`studio-cli__row` for the Agents/CLI list,
 * `studio-tree-row--open-tab` for the Open-tabs list) so every flat row shares
 * the same height, gap, padding, hover wash and selected state.
 *
 * Two shapes:
 *   - static (default): a non-interactive `<div>` (e.g. the CLI agent rows).
 *   - interactive (`[interactive]="true"`): the whole row is a button that
 *     emits `activated` on click and shows the `--active` selection wash when
 *     `[active]` is set (e.g. the Open-tabs rows).
 *
 * `[capitalize]` capitalises the label (the CLI list shows raw `cliType`
 * strings title-cased). `[indent]` overrides the left padding for rows that
 * must align to a surrounding grid (the Open-tabs rows align to the Explorer
 * tree's project-glyph column, not the default 12px gutter).
 */
export declare class ListRowComponent {
    readonly label: import("@angular/core").InputSignal<string>;
    /** Render as a clickable button (emits `activated`) instead of a `<div>`. */
    readonly interactive: import("@angular/core").InputSignal<boolean>;
    /** Selected-row wash; only meaningful with `[interactive]`. */
    readonly active: import("@angular/core").InputSignal<boolean>;
    /** Title-case the label (used by the Agents/CLI list). */
    readonly capitalize: import("@angular/core").InputSignal<boolean>;
    /** Left-padding override (CSS length) for rows that align to an outer grid. */
    readonly indent: import("@angular/core").InputSignal<string | null>;
    /** data-testid passthrough. */
    readonly testid: import("@angular/core").InputSignal<string | null>;
    readonly activated: import("@angular/core").OutputEmitterRef<void>;
}
```

## components/media-lightbox/media-lightbox.component.d.ts

```ts
import { MediaLightboxService } from '../../services/media-lightbox.service';
/**
 * Single-instance image lightbox. Mounted on first use at the app shell so every
 * markdown surface (task description, activity-log, chat, info-button,
 * results) opens the same overlay through `MediaLightboxService`.
 *
 * Behaviour:
 *  - Backdrop click closes.
 *  - Close button closes.
 *  - Escape closes (via `ModalStackService` so a confirm dialog above the
 *    lightbox still wins).
 *  - Left/Right arrows are captured by MediaLightboxService, including
 *    the brief interval before this deferred component mounts.
 *  - Large images are constrained to the viewport but allow zoom-to-actual
 *    via the click-on-image affordance (toggles `object-fit: contain`
 *    vs intrinsic size + scroll container).
 */
export declare class MediaLightboxComponent {
    readonly lightbox: MediaLightboxService;
    private readonly modalStack;
    private readonly destroyRef;
    private stackDispose;
    /**
     * Original-size toggle. Off (default) fits the image into the fixed
     * stage (`object-fit: contain`, never upscaled); on shows it at its
     * intrinsic pixel size inside a scrollable stage. Reset on every image
     * change so paging always starts from the fitted view.
     */
    readonly zoomed: import("@angular/core").WritableSignal<boolean>;
    constructor();
    prev(): void;
    next(): void;
    close(): void;
    /** Toggle intrinsic-size view; click on the image. */
    toggleZoom(event: Event): void;
    runAction(action: {
        run: () => void | Promise<void>;
    }): Promise<void>;
}
```

## components/menu/menu.component.d.ts

```ts
import { OnDestroy } from '@angular/core';
import { MenuItem, MenuItemClickEvent, MenuPlacement, MenuRow } from './menu.types';
/**
 * <app-menu> — F23 shared context-menu surface.
 *
 * One place to render every dropdown / right-click / picker menu in the app.
 * Replaces five hand-rolled menu implementations (devtools menu, studio tab
 * context menu, studio project picker, etc.) so spacing, hover behaviour,
 * keyboard nav, focus management, and theming stay in sync.
 *
 * The component is purely presentational: the caller owns the open signal,
 * provides items, and reacts to `itemClick` + `closeRequest`. Positioning
 * accepts either an `anchorEl` (typical dropdown next to a trigger) or an
 * absolute viewport `position` (right-click context menus). The panel is
 * `position: fixed` so it escapes parent stacking / overflow contexts.
 *
 * Keyboard:
 *   - Esc                 → focus returns to the trigger, closeRequest
 *   - ArrowDown / ArrowUp → move focus across rows, skipping headers,
 *                           separators, and disabled rows
 *   - Home / End          → first / last focusable row
 *   - Enter / Space       → activate row → focus returns to the trigger,
 *                           itemClick + closeRequest
 *
 * Aria:
 *   - panel: role="menu"
 *   - row: role="menuitem", aria-disabled, aria-current for active rows
 *   - separator: role="separator"
 *
 * testIds: `{testIdPrefix}-panel` and `{testIdPrefix}-item-{id}` per row.
 *
 * Strictly tokenised SCSS (no raw hex) so dark + light themes stay in sync.
 */
export declare class MenuComponent implements OnDestroy {
    readonly items: import("@angular/core").InputSignal<readonly MenuItem[]>;
    readonly open: import("@angular/core").InputSignal<boolean>;
    readonly anchorEl: import("@angular/core").InputSignal<HTMLElement | null>;
    /** Viewport-relative coordinate for right-click context menus. */
    readonly position: import("@angular/core").InputSignal<{
        x: number;
        y: number;
    } | null>;
    readonly placement: import("@angular/core").InputSignal<MenuPlacement>;
    readonly testIdPrefix: import("@angular/core").InputSignal<string>;
    readonly minWidth: import("@angular/core").InputSignal<number | null>;
    readonly ariaLabel: import("@angular/core").InputSignal<string | null>;
    readonly itemClick: import("@angular/core").OutputEmitterRef<MenuItemClickEvent>;
    readonly closeRequest: import("@angular/core").OutputEmitterRef<void>;
    private panelRef;
    private readonly cdr;
    private readonly host;
    private readonly modalStack;
    private readonly overlayPortal;
    private readonly destroyRef;
    private modalStackDispose;
    /** Element that had focus when the menu opened; Escape and selection return focus to it. */
    private opener;
    private portalRef;
    private repositionAttached;
    private readonly reposition;
    /** Computed list of focusable row indices (skips header / separator / disabled). */
    readonly focusableIndices: import("@angular/core").Signal<readonly number[]>;
    /** Index of the currently focused row in `items()`, or null if none. */
    readonly focusedIndex: import("@angular/core").WritableSignal<number | null>;
    /** Inline-style position object applied to the panel. */
    readonly panelStyle: import("@angular/core").WritableSignal<{
        top: string;
        left: string;
        minWidth?: string;
    }>;
    constructor();
    ngOnDestroy(): void;
    private acquireModalStack;
    private releaseModalStack;
    private acquirePortal;
    private releasePortal;
    onBackdropClick(): void;
    /** Keyboard close: focus goes back to the trigger before the panel is removed. */
    private close;
    /**
     * Focus the trigger first, so a dialog that the selected row opens records
     * the trigger, not the removed row, as the element to return to.
     */
    private restoreFocus;
    private captureOpener;
    onRowClick(index: number): void;
    onRowMouseEnter(index: number): void;
    /**
     * Panel-level keyboard nav. Bound on the panel root so it fires regardless
     * of which row currently has focus.
     */
    onPanelKeyDown(event: KeyboardEvent): void;
    /**
     * Global key listener so Escape closes the menu when focus has drifted
     * outside the panel (e.g. anchor button retained focus).
     */
    onDocumentKeyDown(event: Event): void;
    isRow(item: MenuItem): item is MenuRow;
    isFocused(index: number): boolean;
    rowTestId(item: MenuRow): string;
    panelTestId(): string;
    private recomputePosition;
    private attachReposition;
    private detachReposition;
    private minWidthCss;
    private moveFocus;
    private focusRow;
}
```

## components/menu/menu.types.d.ts

```ts
/**
 * MenuItem — discriminated union driving <app-menu>.
 *
 * Three kinds:
 *   - `row`       a clickable row. `id` round-trips through the click output.
 *   - `separator` a thin hairline; never focusable, never clickable.
 *   - `header`    a small uppercase section label; never focusable, never clickable.
 *
 * Menu rows are **text-only**. No `icon` field — see the "Menu surfaces are
 * text-only" convention in AGENTS.md. `leadingGlyph` is the one allowed
 * leading affordance and is reserved for project-picker-style coloured
 * initial chips, not for decorative icons.
 *
 * Optional row fields:
 *
 *   - `leadingGlyph` — coloured circular initial (project picker chip).
 *   - `trailingBadge` — count chip on the right (project picker job count).
 *   - `tooltip` — passed to the existing [cacTooltip] directive.
 */
export type MenuItem = MenuRow | {
    kind: 'separator';
} | {
    kind: 'header';
    label: string;
};
export interface MenuRow {
    kind: 'row';
    id: string;
    label: string;
    hint?: string;
    disabled?: boolean;
    danger?: boolean;
    active?: boolean;
    tooltip?: string;
    leadingGlyph?: {
        background: string;
        initial: string;
    };
    trailingBadge?: string;
}
export interface MenuItemClickEvent {
    id: string;
    item: MenuRow;
}
export type MenuPlacement = 'below' | 'above' | 'right';
```

## components/notification/notification.component.d.ts

```ts
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
export declare class NotificationComponent {
    /** Severity / colour variant. Default 'info'. */
    readonly kind: import("@angular/core").InputSignal<NotificationKind>;
    /** Visual density. Toasts float (popover elevation); banners are flat in-flow. */
    readonly layout: import("@angular/core").InputSignal<NotificationLayout>;
    /** Override the default glyph for the chosen `kind`. */
    readonly icon: import("@angular/core").InputSignal<string | null>;
    /** Optional bold heading rendered above the body. */
    readonly title: import("@angular/core").InputSignal<string | null>;
    /**
     * When `true`, render the trailing close button. The component does
     * not own dismissal state; the host listens to `(closeRequest)` and
     * unmounts / hides accordingly.
     */
    readonly closable: import("@angular/core").InputSignal<boolean>;
    /** ARIA live region politeness. Errors / warnings default to assertive. */
    readonly ariaLive: import("@angular/core").InputSignal<"polite" | "assertive" | null>;
    /** Pass-through data-testid for stable selection in Playwright. */
    readonly testid: import("@angular/core").InputSignal<string | null>;
    readonly closeRequest: import("@angular/core").OutputEmitterRef<void>;
    readonly resolvedIcon: import("@angular/core").Signal<string>;
    readonly resolvedAriaLive: import("@angular/core").Signal<"polite" | "assertive">;
}
```

## components/pane-header/pane-header.component.d.ts

```ts
import { StudioIconName } from '../studio-icon/studio-icon.component';
/**
 * The detail-view pane header: icon + title + actions slot + maximize /
 * hide buttons used by the prompt / protocol / git panes. The prompt
 * pane's three-tab strip renders inside the projected `tabs` slot instead
 * of the title; pass an empty title in that case and project the tabs.
 *
 * The sidebar/explorer collapsible group headers (Workspaces, Open tabs,
 * Agents / CLI, …) live in the separate `app-section-header`; this
 * component is pane chrome only.
 *
 * See docs/quality/frontend/audits/scss-quality.md "Wave C".
 */
export declare class PaneHeaderComponent {
    readonly icon: import("@angular/core").InputSignal<StudioIconName | null>;
    /**
     * Optional emoji/glyph icon. Used by panes whose title still carries a
     * Unicode glyph (e.g. the protocol pane's "📜 Protocol"). Renders next
     * to or instead of {@link icon} so callers can opt into the legacy
     * emoji vocabulary without losing the shared chrome.
     */
    readonly iconEmoji: import("@angular/core").InputSignal<string | null>;
    readonly title: import("@angular/core").InputSignal<string>;
    readonly maximized: import("@angular/core").InputSignal<boolean>;
    readonly maximizable: import("@angular/core").InputSignal<boolean>;
    readonly hideable: import("@angular/core").InputSignal<boolean>;
    /** Optional extra subtitle that follows the title. */
    readonly subtitle: import("@angular/core").InputSignal<string | null>;
    /** data-testid passthrough. */
    readonly testid: import("@angular/core").InputSignal<string | null>;
    /** Optional modifier class applied to the host header (e.g. brand wash). */
    readonly modifier: import("@angular/core").InputSignal<string | null>;
    readonly maximize: import("@angular/core").OutputEmitterRef<void>;
    readonly hide: import("@angular/core").OutputEmitterRef<void>;
}
```

## components/pane-tabs/pane-tabs.component.d.ts

```ts
import { AfterViewInit, OnDestroy } from '@angular/core';
import { StudioIconName } from '../studio-icon/studio-icon.component';
import type { MenuItem, MenuItemClickEvent } from '../menu/menu.types';
/**
 * Shape of a single tab in the shared {@link PaneTabsComponent} strip.
 *
 * The component is intentionally data-driven (vs. content projection per
 * tab) so panes can hand it an array of tabs and the visual chrome —
 * active-state, hover, focus, badge slot, indicator slot — stays
 * identical across surfaces.
 */
export interface PaneTabDef {
    /** Stable identifier emitted via `tabChange`. */
    readonly id: string;
    /** User-facing label rendered next to the icon. */
    readonly label: string;
    /** Optional studio-icon (preferred over emoji when both supplied). */
    readonly icon?: StudioIconName;
    /** Optional emoji/glyph; rendered when {@link icon} is omitted. */
    readonly emoji?: string;
    /** Numeric badge shown after the label (e.g. evidence count). */
    readonly badge?: number | string | null;
    /** Disables the button but keeps it in the strip. */
    readonly disabled?: boolean;
    /** Stable test hook; falls back to no `data-testid`. */
    readonly testid?: string;
    /**
     * Right-side indicator:
     *  - `spinner` for in-flight async work next to the label
     *  - `live` for a pulsing dot signalling an active stream
     */
    readonly indicator?: 'spinner' | 'live';
    /**
     * Optional CSS class suffix appended to the button (`pane-tab--<suffix>`).
     * Used by callers that want per-tab modifiers like activity ordering;
     * the component itself never reads it.
     */
    readonly modifier?: string;
}
export type PaneTabsVariant = 'header' | 'pill';
/**
 * Shared tab control used by panes that surface multiple sub-views in a
 * single header. Two visual variants:
 *
 *   - `header` — the canonical pane-header tab strip used by the prompt
 *     pane (Description / Evidence / Code Review). Full-height cells
 *     with a bottom-border accent on the active tab; sits inside the
 *     `tabs` slot of `<app-pane-header>`.
 *   - `pill`   — a compact pill toggle group used inside a pane body
 *     (e.g. the protocol pane's Protocol / Activity inspector switch).
 *
 * The component owns ARIA wiring (`role="tablist"` / `role="tab"`,
 * `aria-selected`) so every caller gets accessible tabs by default.
 */
export declare class PaneTabsComponent implements AfterViewInit, OnDestroy {
    private readonly host;
    private resizeObserver;
    private measurementFrame;
    private viewReady;
    private readonly onFontsLoaded;
    readonly tabs: import("@angular/core").InputSignal<readonly PaneTabDef[]>;
    readonly activeTabId: import("@angular/core").InputSignal<string>;
    readonly variant: import("@angular/core").InputSignal<PaneTabsVariant>;
    /** Optional modifier suffix applied to the container (e.g. `activity-first`). */
    readonly listModifier: import("@angular/core").InputSignal<string | null>;
    /** `aria-label` for the tablist container. */
    readonly ariaLabel: import("@angular/core").InputSignal<string | null>;
    /** ID of the content panel controlled by this strip. */
    readonly panelId: import("@angular/core").InputSignal<string>;
    /** Minimum readable width reserved for each inline tab. */
    readonly minimumTabWidth: import("@angular/core").InputSignal<number>;
    readonly tabChange: import("@angular/core").OutputEmitterRef<string>;
    readonly overflowOpen: import("@angular/core").WritableSignal<boolean>;
    readonly overflowAnchor: import("@angular/core").WritableSignal<HTMLElement | null>;
    readonly availableWidth: import("@angular/core").WritableSignal<number | null>;
    readonly measuredTabWidths: import("@angular/core").WritableSignal<Readonly<Record<string, number>>>;
    readonly measuredOverflowWidth: import("@angular/core").WritableSignal<number>;
    readonly focusedTabId: import("@angular/core").WritableSignal<string | null>;
    private readonly tabMeasurementEffect;
    readonly inlineTabs: import("@angular/core").Signal<readonly PaneTabDef[]>;
    readonly overflowTabs: import("@angular/core").Signal<readonly PaneTabDef[]>;
    readonly overflowMenuItems: import("@angular/core").Signal<readonly MenuItem[]>;
    readonly overflowAriaLabel: import("@angular/core").Signal<string>;
    readonly containerClass: import("@angular/core").Signal<string>;
    ngAfterViewInit(): void;
    ngOnDestroy(): void;
    private scheduleMeasurement;
    private measureTabs;
    trackTab(_index: number, tab: PaneTabDef): string;
    tabClass(tab: PaneTabDef): Record<string, boolean>;
    onClick(tab: PaneTabDef): void;
    tabId(tab: PaneTabDef): string;
    tabIndex(tab: PaneTabDef): number;
    onTabKeydown(event: KeyboardEvent, tab: PaneTabDef): void;
    toggleOverflow(event: MouseEvent): void;
    closeOverflow(): void;
    onOverflowItemClick(event: MenuItemClickEvent): void;
}
```

## components/resizable-splitter/resizable-splitter.directive.d.ts

```ts
import { OnInit } from '@angular/core';
export type SplitterSide = 'start' | 'end';
/** Shared persisted, pointer and keyboard-operable vertical pane splitter. */
export declare class ResizableSplitterDirective implements OnInit {
    private readonly host;
    readonly splitterStorageKey: import("@angular/core").InputSignal<string>;
    readonly splitterCssProperty: import("@angular/core").InputSignal<string>;
    readonly splitterDefaultSize: import("@angular/core").InputSignal<number>;
    readonly splitterMinSize: import("@angular/core").InputSignal<number>;
    readonly splitterMinOppositeSize: import("@angular/core").InputSignal<number>;
    readonly splitterSide: import("@angular/core").InputSignal<SplitterSide>;
    readonly splitterStep: import("@angular/core").InputSignal<number>;
    readonly role = "separator";
    readonly orientation = "vertical";
    readonly tabIndex = 0;
    get ariaMin(): number;
    get ariaMax(): number;
    get ariaNow(): number;
    private size;
    private drag;
    ngOnInit(): void;
    onPointerDown(event: PointerEvent): void;
    onPointerMove(event: PointerEvent): void;
    onPointerEnd(event: PointerEvent): void;
    onKeydown(event: KeyboardEvent): void;
    onDoubleClick(event: MouseEvent): void;
    private setSize;
    private minimum;
    private maximum;
    private container;
    private readSize;
    private persist;
}
```

## components/row/row.component.d.ts

```ts
/**
 * Shared label/value row primitive. Variants resolve to the density tokens
 * declared in `_tokens-semantic.scss` (`--studio-row-pad-block-*`,
 * `--studio-row-min-h-*`, `--studio-row-gap-*`). Use this anywhere the UI
 * shows a flat "label on the left, value on the right" pattern (status
 * facts, settings entries, activity facts) and you want it to track the
 * project-wide density rules instead of carrying its own px.
 *
 * Slots project via attribute selectors so the caller does not need an
 * extra wrapper element:
 *   <app-row variant="compact">
 *     <span appRowLabel>Lane</span>
 *     <span appRowValue>Ready</span>
 *   </app-row>
 *
 * `variant`:
 *   - compact (default) — operator surfaces (Overview, Explorer, Activity)
 *   - default           — mid-density panels
 *   - cozy              — empty states, Welcome cards
 *
 * `interactive`:
 *   - false (default)   — static row, smaller min-height floor
 *   - true              — clickable / keyboard-focusable, lifts the floor
 *                         to the WCAG-AA touch target (32/36/40px)
 */
export declare class RowComponent {
    readonly variant: import("@angular/core").InputSignal<"default" | "compact" | "cozy">;
    readonly interactive: import("@angular/core").InputSignal<boolean>;
}
```

## components/section-header/section-header.component.d.ts

```ts
import { StudioIconName } from '../studio-icon/studio-icon.component';
/**
 * The single canonical sidebar section header. Every panel group head in
 * the studio shell (Explorer / Workspaces, Agents / CLI, Runbook,
 * Orchestrator, Settings groups, Open tabs) renders through this so the
 * uppercase label, padding, divider rhythm and count pill are identical
 * across panels. It absorbed the two formerly-divergent surfaces: the
 * `app-pane-header[collapsible]` section mode and the bare
 * `studio-explorer__group-head` <div>.
 *
 * Two shapes:
 *   - static (default): a non-interactive heading <div>.
 *   - collapsible (`[collapsible]="true"`): the whole row is a toggle
 *     button — the shared `<app-disclosure-marker>` (guideline rule
 *     ADM-17) flips between expanded / collapsed and
 *     `collapsedChange` fires the flipped state so the parent can persist
 *     it (see ExplorerSectionsService). `aria-expanded` + the caller's
 *     `testid` are the contract the F27/F46 explorer-collapse specs
 *     assert on.
 *
 * `[divider]="true"` adds the top rule + spacing used to separate a
 * group from the one above it (formerly `studio-explorer__group-head--mt`).
 * The optional `[actions]` slot hosts inline affordances (e.g. the
 * Workspaces "show all" button).
 */
export declare class SectionHeaderComponent {
    readonly title: import("@angular/core").InputSignal<string>;
    /** Optional count pill shown after the title. */
    readonly count: import("@angular/core").InputSignal<string | number | null>;
    /** Optional leading SVG glyph. */
    readonly icon: import("@angular/core").InputSignal<StudioIconName | null>;
    /** data-testid passthrough. */
    readonly testid: import("@angular/core").InputSignal<string | null>;
    /** Top rule + spacing that separates this group from the one above. */
    readonly divider: import("@angular/core").InputSignal<boolean>;
    /** Renders the row as a collapse toggle button instead of a heading. */
    readonly collapsible: import("@angular/core").InputSignal<boolean>;
    readonly collapsed: import("@angular/core").InputSignal<boolean>;
    readonly collapsedChange: import("@angular/core").OutputEmitterRef<boolean>;
    onCollapseToggle(ev: Event): void;
}
```

## components/segmented-control/segmented-control.component.d.ts

```ts
import { StudioIconName } from '../studio-icon/studio-icon.component';
/**
 * One choice in a {@link SegmentedControlComponent}.
 *
 * The control is data-driven (vs. content projection per option) so every
 * caller hands it an array and the visual chrome: raised thumb, dimmed
 * inactive, hover, focus ring — stays identical across surfaces.
 */
export interface SegmentedOption<T extends string | number = string> {
    /** Stable value emitted via `valueChange` and compared against `value`. */
    readonly value: T;
    /** User-facing label rendered in the segment. */
    readonly label: string;
    /** Optional studio icon rendered before the label; omitted → text only. */
    readonly icon?: StudioIconName;
    /** Stable test hook; falls back to no `data-testid`. */
    readonly testid?: string;
    /** Disables this one segment but keeps it in the strip. */
    readonly disabled?: boolean;
    /** Optional accessible tooltip for a choice. */
    readonly tooltip?: string;
}
/**
 * Shared two-or-more option segmented control (single-select toggle).
 *
 * Used for the Settings Appearance/Layout switches (Theme Dark|Light,
 * Activity bar Left|Right) and any future "pick one of N" control that
 * wants the same look. Renders a `role="group"` container with one
 * `<button>` per option; the selected option carries `aria-pressed="true"`
 * and a raised thumb, the rest stay dimmed, so the active choice reads
 * at a glance in both light and dark themes.
 *
 * The group is one Tab stop: Tab lands on the selected option and the arrow
 * keys move between options; Space or Enter selects (AGT-3834).
 */
export declare class SegmentedControlComponent<T extends string | number = string> {
    readonly options: import("@angular/core").InputSignal<readonly SegmentedOption<T>[]>;
    readonly value: import("@angular/core").InputSignal<T>;
    /** Accessible name for the whole group (e.g. "Theme", "Activity bar"). */
    readonly ariaLabel: import("@angular/core").InputSignal<string | null>;
    /** Disables the full choice group during an operation. */
    readonly disabled: import("@angular/core").InputSignal<boolean>;
    /** False when an enclosing toolbar owns the arrow-key set (see RovingFocusDirective). */
    readonly rovingFocus: import("@angular/core").InputSignal<boolean>;
    readonly valueChange: import("@angular/core").OutputEmitterRef<T>;
    trackOption(_index: number, option: SegmentedOption<T>): string | number;
    select(option: SegmentedOption<T>): void;
}
```

## components/sidesheet/sidesheet.component.d.ts

```ts
/**
 * Reusable sidesheet skeleton — strictly for **side panels** pinned to
 * the right edge of the viewport (kanban filter, CLI usage,
 * orchestrator chat, workspace screenshots, etc.). Owns the layout,
 * theming, close button, and slot projection so call sites only have
 * to ship their inner content.
 *
 * **Not** intended for modal dialogs (error / confirm / create-job /
 * media-lightbox / verbose-debug). Those have different semantics —
 * backdrop click-to-close, alertdialog ARIA role, focus trap,
 * shaped specifically for one decision. They keep their own
 * components or migrate to a future `<app-dialog>` skeleton when one
 * exists.
 *
 * See docs/quality/frontend/audits/scss-quality.md "Wave B" for the migration plan.
 *
 * Usage:
 *
 *   <app-sidesheet
 *     eyebrow="BOARD"
 *     title="Filter & view"
 *     (close)="onClose()">
 *     <ng-container body>
 *       ... your panel body ...
 *     </ng-container>
 *     <div footer class="my-footer-bits">
 *       <button>Clear</button>
 *     </div>
 *   </app-sidesheet>
 */
export declare class SidesheetComponent {
    readonly eyebrow: import("@angular/core").InputSignal<string | null>;
    readonly title: import("@angular/core").InputSignal<string>;
    /** Optional one-line caption under the title. */
    readonly subtitle: import("@angular/core").InputSignal<string | null>;
    /** Optional width override (px). Default 360. */
    readonly width: import("@angular/core").InputSignal<number | null>;
    /** Hides the close button when set to `false`. */
    readonly closable: import("@angular/core").InputSignal<boolean>;
    /** data-testid passthrough for stable element selection. */
    readonly testid: import("@angular/core").InputSignal<string | null>;
    readonly closeRequest: import("@angular/core").OutputEmitterRef<void>;
}
```

## components/status-indicator/status-indicator.component.d.ts

```ts
import { type StudioIconName } from '../studio-icon/studio-icon.component';
/** The six tones every status display maps onto (AGT-3402). */
export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'running';
/** One icon per tone, so the meaning never rests on colour alone. */
export declare const STATUS_TONE_ICONS: Readonly<Record<StatusTone, StudioIconName>>;
/**
 * The shared status label: a tone icon in front of a visible text label,
 * tinted from the `--studio-status-*` tokens. The icon is decorative
 * (`aria-hidden`); the label is the accessible text. An optional `detail`
 * becomes a tooltip and makes the indicator focusable so keyboard users can
 * read it too. `compact` drops the tinted chip for dense rows and tables.
 *
 *   <app-status-indicator tone="success" label="Connected" />
 *
 * Contract and state-to-tone mapping: docs/quality/design/status-indicator.md.
 */
export declare class StatusIndicatorComponent {
    readonly tone: import("@angular/core").InputSignal<StatusTone>;
    readonly label: import("@angular/core").InputSignal<string>;
    /** Longer explanation shown as a tooltip; `null` or empty shows none. */
    readonly detail: import("@angular/core").InputSignal<string | null | undefined>;
    readonly compact: import("@angular/core").InputSignal<boolean>;
    readonly wrap: import("@angular/core").InputSignal<boolean>;
    readonly testId: import("@angular/core").InputSignal<string>;
    readonly icon: import("@angular/core").Signal<StudioIconName>;
    readonly tooltip: import("@angular/core").Signal<string | null>;
}
```

## components/studio-icon/studio-icon.component.d.ts

```ts
/**
 * Inline-SVG icon set for the Agent Software Studio shell.
 *
 * Ported 1:1 from the reference layout's icons.jsx (see
 * .reference-layout/icons.jsx in the agent-orchestrator zip). Every path
 * is a VS Code-style line icon on a 24×24 grid; the component renders at
 * `[size]` pixels and inherits `currentColor` so it tints with the host's
 * `color`. This is the canonical icon source — UI surfaces (titlebar,
 * activity bar, sidebar, tab strip, status bar, evidence cards) should
 * use this component instead of Unicode glyphs or emoji.
 */
export type StudioIconName = 'folder' | 'file' | 'code' | 'list' | 'filter' | 'search' | 'cli' | 'activity' | 'message' | 'settings' | 'plus' | 'close' | 'dot' | 'check' | 'expand' | 'collapse' | 'play' | 'pause' | 'branch' | 'refresh' | 'more' | 'bell' | 'warn' | 'diff' | 'book' | 'eye' | 'panelLeft' | 'panelRight' | 'layout' | 'sliders' | 'bot' | 'grid' | 'deck' | 'archive' | 'send' | 'sun' | 'moon' | 'pin' | 'backlog' | 'link' | 'star' | 'starFilled' | 'drag' | 'chevronRight' | 'chevronDown' | 'chevronLeft' | 'clock' | 'help' | 'info' | 'spinner' | 'circle';
export declare class StudioIconComponent {
    readonly name: import("@angular/core").InputSignal<StudioIconName>;
    readonly size: import("@angular/core").InputSignal<number>;
    readonly strokeWidth: import("@angular/core").InputSignal<number>;
}
```

## components/studio-sidebar-header/studio-sidebar-header.component.d.ts

```ts
/**
 * Shared Studio sidebar header chrome. Explorer, Filters, and Project Hub use
 * the same compact title/action row instead of each feature carrying its own
 * header spacing.
 */
export declare class StudioSidebarHeaderComponent {
    readonly title: import("@angular/core").InputSignal<string>;
    readonly subtitle: import("@angular/core").InputSignal<string | null>;
    readonly subtitleTestid: import("@angular/core").InputSignal<string | null>;
    readonly testid: import("@angular/core").InputSignal<string | null>;
}
```

## components/tooltip/app-tooltip.directive.d.ts

```ts
import { OnDestroy } from '@angular/core';
export declare class AppTooltipDirective implements OnDestroy {
    readonly appTooltip: import("@angular/core").InputSignal<string | null>;
    readonly appTooltipTestId: import("@angular/core").InputSignal<string | null>;
    readonly appTooltipDelay: import("@angular/core").InputSignal<number>;
    readonly appTooltipLinks: import("@angular/core").InputSignal<readonly {
        label: string;
        href: string;
    }[]>;
    private readonly host;
    private readonly document;
    private readonly tooltipId;
    private showTimer;
    private hideTimer;
    private overlay;
    scheduleShow(): void;
    onFocusOut(event: FocusEvent): void;
    hide(): void;
    ngOnDestroy(): void;
    private show;
    private readonly position;
    private addDescription;
    private removeDescription;
    private cancelShow;
    scheduleHide(): void;
    private cancelHide;
}
```

## components/tooltip/legacy-tooltip.directive.d.ts

```ts
import { InjectionToken, OnDestroy } from '@angular/core';
export interface TooltipRenderer {
    show(anchor: HTMLElement, content: string, position: 'auto' | 'top' | 'bottom' | 'left' | 'right', severity?: 'info' | 'warn' | 'error' | 'success'): void;
    hide(anchor: HTMLElement): void;
}
export declare const STUDIO_TOOLTIP_RENDERER: InjectionToken<TooltipRenderer>;
export declare class LegacyTooltipDirective implements OnDestroy {
    readonly content: import("@angular/core").InputSignal<string>;
    readonly tooltipPosition: import("@angular/core").InputSignal<"right" | "left" | "auto" | "top" | "bottom">;
    readonly tooltipSeverity: import("@angular/core").InputSignal<"success" | "info" | "error" | "warn" | undefined>;
    private readonly host;
    private readonly renderer;
    private touchTimer;
    show(): void;
    hide(): void;
    onTouchStart(): void;
    onDocumentTouch(event: TouchEvent): void;
    ngOnDestroy(): void;
    private clearTimer;
}
```

## components/tree-row/tree-row.component.d.ts

```ts
import { StudioIconName } from '../studio-icon/studio-icon.component';
/**
 * Reusable tree row used in the studio shell sidebar (Explorer
 * workspace tree, Tasks outline, legacy `.tree-row`). Centralises the
 * chevron + glyph + label + count layout so the three places that
 * repeat the pattern read from one source.
 *
 * Outline:
 *   [chevron] [glyph] label [meta] [count badge]
 *
 * Caller chooses which slots to fill via inputs. Two visual variants:
 *   - level="root"  — workspace / project rows (8 px left padding)
 *   - level="child" — sub rows (44 px left padding to nest under the
 *                     parent's glyph)
 *
 * The component preserves the legacy BEM class names so existing
 * styles.scss bridge entries continue to apply without rewrite.
 */
export declare class TreeRowComponent {
    /** Visible chevron — pass `null` to suppress the chevron column. */
    readonly chevron: import("@angular/core").InputSignal<"collapsed" | "expanded" | null>;
    /** Keep the chevron column even when the row itself has no chevron. */
    readonly reserveChevron: import("@angular/core").InputSignal<boolean>;
    /** Optional SVG glyph; takes precedence over `glyphChar`. */
    readonly glyph: import("@angular/core").InputSignal<StudioIconName | null>;
    /** Optional initial letter (e.g. project avatar "A"). */
    readonly glyphChar: import("@angular/core").InputSignal<string | null>;
    /** Keep the glyph column even when the row has no icon or initial. */
    readonly reserveGlyph: import("@angular/core").InputSignal<boolean>;
    /** Coloured square behind the glyph initial. */
    readonly glyphColor: import("@angular/core").InputSignal<string | null>;
    readonly label: import("@angular/core").InputSignal<string>;
    readonly count: import("@angular/core").InputSignal<string | number | null>;
    readonly meta: import("@angular/core").InputSignal<string | null>;
    /** Complete row context shown through the viewport-safe shared tooltip. */
    readonly tooltip: import("@angular/core").InputSignal<string | null>;
    readonly tooltipTestId: import("@angular/core").InputSignal<string | null>;
    readonly ariaLabel: import("@angular/core").InputSignal<string | null>;
    readonly ariaControls: import("@angular/core").InputSignal<string | null>;
    readonly active: import("@angular/core").InputSignal<boolean>;
    readonly level: import("@angular/core").InputSignal<"root" | "child">;
    readonly testid: import("@angular/core").InputSignal<string | null>;
    /** `aria-current` value for the row button (e.g. 'page' for the active nav item). */
    readonly ariaCurrent: import("@angular/core").InputSignal<"true" | "page" | null>;
    /** data-testid for the chevron control — lets callers assert on the disclosure twisty. */
    readonly chevronTestid: import("@angular/core").InputSignal<string | null>;
    readonly disabled: import("@angular/core").InputSignal<boolean>;
    /** Reserve an independent trailing action beside the row button. */
    readonly hasActions: import("@angular/core").InputSignal<boolean>;
    /**
     * Visible text the accessible name must start with (WCAG 2.5.3). Defaults
     * to the rendered row text; set it when projected content is row metadata
     * (dates, status chips) that the `ariaLabel` deliberately leaves out.
     */
    readonly visibleName: import("@angular/core").InputSignal<string | null>;
    private readonly row;
    /** Rendered text of the row button, projected content included. */
    private readonly renderedText;
    /** `ariaLabel` as detail after the row's visible text (WCAG 2.5.3). */
    readonly accessibleName: import("@angular/core").Signal<string | null>;
    constructor();
    readonly chevronClick: import("@angular/core").OutputEmitterRef<Event>;
    readonly selectRequest: import("@angular/core").OutputEmitterRef<Event>;
    readonly secondary: import("@angular/core").OutputEmitterRef<Event>;
    onChevronClick(ev: Event): void;
    onSelect(ev: Event): void;
    onSecondary(ev: Event): void;
}
```

## directives/modal-focus.directive.d.ts

```ts
import { AfterViewInit, OnDestroy } from '@angular/core';
/**
 * Keyboard contract of an `aria-modal` surface (AGT-3831).
 *
 * - Tab and Shift+Tab cycle inside the host (CDK focus-trap anchors).
 * - On open, focus moves into the host unless the owner already put it
 *   there: the host itself when it carries a `tabindex`, else its first
 *   tabbable control, else the host made focusable.
 * - On close, focus returns to the element that had it when the surface
 *   opened, unless the owner already moved focus somewhere outside.
 *
 * Escape stays with the owner and `ModalStackService`; this directive only
 * owns where focus is.
 */
export declare class ModalFocusDirective implements AfterViewInit, OnDestroy {
    private readonly host;
    private readonly injector;
    private readonly trap;
    private readonly opener;
    ngAfterViewInit(): void;
    ngOnDestroy(): void;
}
/**
 * True while `element` holds focus that a closing surface handed back to its
 * opener and the user has not moved since. A follow-up surface, such as the
 * card a create dialog just made, may take that focus over (AGT-3834).
 */
export declare function holdsReturnedFocus(element: Element | null): boolean;
/** The focused element, or null when nothing outside `surface` has focus. */
export declare function focusedElementOutside(surface: HTMLElement): HTMLElement | null;
/**
 * Moves focus back to `opener` when focus would otherwise be lost: it sits
 * on the body, inside the closing `surface`, or on a removed node. A focus
 * the owner moved elsewhere on purpose is kept.
 */
export declare function returnFocusToOpener(opener: HTMLElement | null, surface: HTMLElement): void;
```

## directives/overlay-portal.directive.d.ts

```ts
import { OnDestroy, OnInit } from '@angular/core';
import { OverlayPortalLayer } from '../services/overlay-portal.service';
/**
 * Template-level bridge to the central overlay layer.
 *
 * Use this on modal, popover, dropdown, and popup root elements that are
 * conditionally rendered by Angular templates. The directive keeps the owning
 * component's bindings intact while moving the actual DOM node to <body>, so
 * fixed-position overlays are not clipped by ancestor overflow or local
 * stacking contexts.
 */
export declare class OverlayPortalDirective implements OnInit, OnDestroy {
    readonly appOverlayPortal: import("@angular/core").InputSignal<OverlayPortalLayer>;
    private readonly host;
    private readonly overlayPortal;
    private portalRef;
    ngOnInit(): void;
    ngOnDestroy(): void;
}
```

## directives/roving-focus.directive.d.ts

```ts
/**
 * Makes a group of buttons one Tab stop with arrow-key movement (AGT-3834).
 *
 * Every descendant marked `data-roving-item` takes part. Exactly one enabled
 * item keeps `tabindex="0"`: the focused item while focus is inside, else the
 * first pressed or checked item, else the one last focused, else the first
 * enabled item. The others get `tabindex="-1"`.
 * Arrow keys move focus (wrapping), Home and End jump to the ends. Arrows
 * only move focus; Space and Enter still activate the focused button, so a
 * group of pick-one or pick-many toggles keeps its `aria-pressed` semantics.
 *
 * `appRovingFocus` set to `false` leaves the items alone, so an enclosing
 * group (for example a toolbar around a segmented control and a switch) can
 * own the roving set instead.
 */
export declare class RovingFocusDirective {
    readonly appRovingFocus: import("@angular/core").InputSignal<boolean | "">;
    private readonly host;
    private current;
    constructor();
    private enabled;
    items(): HTMLElement[];
    /** Re-assign the single Tab stop. Public so specs can drive it without a render. */
    sync(leaving?: boolean): void;
    onFocusin(event: FocusEvent): void;
    /** Leaving the group hands the stop back to the selected item. */
    onFocusout(event: FocusEvent): void;
    onKeydown(event: KeyboardEvent): void;
}
```

## models/app-dialog.model.d.ts

```ts
/**
 * Shared models for the unified app dialog family:
 *  - confirm dialogs (delete / discard / load-all-style guard rails)
 *  - notification popups (success / info / warning / error)
 *
 * The full error overlay with stack trace + copy lives in
 * `error-dialog.model.ts`; this file covers the lighter-weight surfaces
 * that previously fell back to `window.confirm` and ad-hoc toasts.
 */
export type NotificationKind = 'success' | 'info' | 'warning' | 'error' | 'accent';
/**
 * Where the toast docks on screen. Defaults to `top-right` (the shared
 * notification pile). `bottom-right` is reserved for toasts that would
 * otherwise collide with a top-right surface — e.g. the Move/Undo action
 * toast, which must not cover the task-detail context menu that opens in
 * the same top-right corner. `below-toolbar` docks under the task tab
 * toolbar and the card's refusal banner, for a refusal toast whose action
 * also lives in that toolbar (AGT-3873); on a narrow screen it spans the
 * width below them.
 */
export type NotificationPosition = 'top-right' | 'bottom-right' | 'below-toolbar';
export type ConfirmDialogKind = 'danger' | 'primary';
export interface NotificationAction {
    label: string;
    testId?: string;
    primary?: boolean;
    callback: () => void;
}
export interface ConfirmDialogOptions {
    title: string;
    message: string;
    /** Context label above the title when the default for this kind is misleading. */
    eyebrow?: string;
    /** Extra body line below the message, e.g. the affected item's name. */
    detail?: string;
    /** Every item the action touches, one per row (AGT-3585 bulk actions list the cards). */
    items?: string[];
    confirmLabel?: string;
    cancelLabel?: string;
    /** Visual flavour. `danger` is the default for delete-style flows. */
    kind?: ConfirmDialogKind;
    /**
     * Type-to-confirm gate. When non-empty, the dialog renders a text input
     * and keeps the confirm button disabled until the trimmed input matches
     * one of these values (case-insensitive). Used by the destructive
     * project-delete flow so the operator must re-type the project name or
     * its short code before the second confirmation can complete.
     */
    requireTypedValues?: string[];
    /** Label shown above the type-to-confirm input. */
    requireTypedPrompt?: string;
    /**
     * Written-reason gate (AGT-3624). When set, the dialog renders a text area
     * and keeps the confirm button disabled until the trimmed reason has at
     * least `minLength` characters. Use `ConfirmDialogService.confirmWithReason`
     * to receive the reason. Archive overrides set the same bar.
     */
    requireReason?: ConfirmReasonGate;
}
export interface ConfirmReasonGate {
    /** Label shown above the reason input. */
    prompt: string;
    minLength: number;
}
export interface ConfirmDialogState extends Required<Omit<ConfirmDialogOptions, 'detail' | 'eyebrow' | 'items' | 'requireTypedValues' | 'requireTypedPrompt' | 'requireReason'>> {
    detail: string | null;
    eyebrow: string | null;
    items: string[] | null;
    requireTypedValues: string[] | null;
    requireTypedPrompt: string | null;
    requireReason: ConfirmReasonGate | null;
}
export interface NotificationOptions {
    message: string;
    title?: string;
    kind: NotificationKind;
    /**
     * Auto-dismiss after this many ms. Pass `0` to keep it open until the
     * user dismisses or the caller calls `dismiss(id)`. When omitted the
     * service picks a sensible default per `kind` (errors stay longer).
     */
    durationMs?: number;
    /**
     * Optional small caption rendered as its own sub-line under the
     * message (typically the project / job slug that produced the
     * notification). Callers must put it here instead of concatenating
     * "... in <project>" onto `message` so the toast layout can clamp the
     * body independently of the source label.
     */
    source?: string;
    /** Optional detail lines rendered below the message (e.g. verification failures). */
    details?: string[];
    /** Action buttons rendered in the toast footer. */
    actions?: NotificationAction[];
    /**
     * Where the toast docks. Omit for the default `top-right` pile; set
     * `bottom-right` to keep a toast clear of a top-right surface it would
     * otherwise occlude (see {@link NotificationPosition}).
     */
    position?: NotificationPosition;
}
export interface NotificationState extends NotificationOptions {
    id: number;
    durationMs: number;
}
```

## models/error-dialog.model.d.ts

```ts
export interface ErrorDialogState {
    title: string;
    message: string;
    output: string;
    stackTrace: string | null;
    source: string | null;
    canOpenCliConfig: boolean;
    /** Set for an expected refusal; the dialog leads with its plain wording. */
    refusal?: ErrorDialogRefusal | null;
}
/** AGT-3624: plain wording of an expected refusal. */
export interface ErrorDialogRefusal {
    /** One plain sentence: why the action was refused. */
    sentence: string;
    /** What the operator can do next. */
    nextAction: string;
}
export interface ErrorDialogOptions {
    title?: string;
    fallbackMessage?: string;
    source?: string;
    canOpenCliConfig?: boolean;
    output?: unknown;
    stackTrace?: string | null;
    refusal?: ErrorDialogRefusal | null;
    /** Action to refocus after the dialog closes, including when Escape closes it. */
    returnFocusSelector?: string;
}
```

## public-api.d.ts

```ts
export * from './components/app-dialog/confirm-dialog/confirm-dialog.component';
export * from './components/app-dialog/notification-stack/notification-stack.component';
export * from './components/async-feedback/loading-surface/loading-surface.component';
export * from './components/async-feedback/pending-button.directive';
export * from './components/copyable-command/copyable-command.component';
export * from './components/count-badge/count-badge.component';
export * from './components/dialog/dialog.component';
export * from './components/disclosure-marker/disclosure-marker.component';
export * from './components/empty-state/empty-state.component';
export * from './components/error-dialog/error-dialog.component';
export * from './components/info-button/info-button-markdown-body/info-button-markdown-body.component';
export * from './components/info-button/info-button.component';
export * from './components/list-row/list-row.component';
export * from './components/media-lightbox/media-lightbox.component';
export * from './components/menu/menu.component';
export * from './components/menu/menu.types';
export * from './components/notification/notification.component';
export * from './components/pane-header/pane-header.component';
export * from './components/pane-tabs/pane-tabs.component';
export * from './components/resizable-splitter/resizable-splitter.directive';
export * from './components/row/row.component';
export * from './components/section-header/section-header.component';
export * from './components/segmented-control/segmented-control.component';
export * from './components/sidesheet/sidesheet.component';
export * from './components/status-indicator/status-indicator.component';
export * from './components/studio-icon/studio-icon.component';
export * from './components/studio-sidebar-header/studio-sidebar-header.component';
export * from './components/tooltip/app-tooltip.directive';
export * from './components/tooltip/legacy-tooltip.directive';
export * from './components/tree-row/tree-row.component';
export * from './directives/modal-focus.directive';
export * from './directives/overlay-portal.directive';
export * from './directives/roving-focus.directive';
export * from './services/confirm-dialog.service';
export * from './services/media-lightbox.service';
export * from './services/modal-stack.service';
export * from './services/notification.service';
export * from './services/overlay-portal.service';
export * from './components/info-button/help-loader';
```

## services/confirm-dialog.service.d.ts

```ts
import { ConfirmDialogKind, ConfirmDialogOptions, ConfirmDialogState } from '../models/app-dialog.model';
/**
 * App-wide confirm dialog. Replaces `window.confirm` so every confirmation
 * shares the Catppuccin-inspired look, focus trap, Esc/Enter handling, and
 * unified button styling defined in the `app-confirm-dialog` component.
 *
 * Usage:
 *   const ok = await confirmDialog.confirm({
 *     title: 'Delete this task?',
 *     message: 'This removes the job folder and all its files.',
 *     detail: `"${job.title}"`,
 *     confirmLabel: 'Delete',
 *     kind: 'danger',
 *   });
 *   if (!ok) return;
 *
 * Only one confirm is open at a time; opening a new one while another is
 * pending auto-rejects the previous prompt (the caller's await resolves
 * with `false`).
 */
export declare class ConfirmDialogService {
    readonly active: import("@angular/core").WritableSignal<ConfirmDialogState | null>;
    private resolver;
    private acceptedReason;
    constructor();
    confirm(options: ConfirmDialogOptions): Promise<boolean>;
    /**
     * Confirm with a written-reason gate. Resolves with the trimmed reason, or
     * null when the operator cancels.
     */
    confirmWithReason(options: ConfirmDialogOptions & {
        requireReason: NonNullable<ConfirmDialogOptions['requireReason']>;
    }): Promise<string | null>;
    accept(reason?: string): void;
    cancel(): void;
    private settle;
}
/** Public constants so call sites can express intent without magic strings. */
export declare const CONFIRM_KIND_DANGER: ConfirmDialogKind;
export declare const CONFIRM_KIND_PRIMARY: ConfirmDialogKind;
```

## services/media-lightbox.service.d.ts

```ts
/**
 * App-wide image lightbox / enlarge view.
 *
 * Markdown surfaces (task description, activity-log, chat, info-button,
 * results) all render images inline through their own renderer; clicking
 * an image should open a single shared overlay that respects the modal
 * stack (Escape, focus trap, etc.) instead of every surface re-inventing
 * its own zoom widget.
 *
 * Two entry points:
 *   inject(MediaLightboxService).open({ src, alt });            // single image
 *   inject(MediaLightboxService).openGallery({ images, index }); // paged set
 *
 * The gallery form lets a surface hand over every image it rendered plus
 * the one that was clicked, so the lightbox can page Prev/Next with the
 * arrow keys instead of the keys leaking up to the task-detail triage
 * handler (which would switch tasks). `open()` is just a one-element
 * gallery so the single-image callers (TipTap description editor, etc.)
 * keep working unchanged.
 *
 * The lightbox component (`<app-media-lightbox>`) mounts in the app shell
 * on first open. It binds `active()` to its template and calls `close()` on
 * dismiss. The service owns the modal-stack registration so any caller
 * gets correct Escape ordering for free.
 */
export interface MediaLightboxRequest {
    readonly src: string;
    readonly alt?: string | null;
    readonly actions?: readonly MediaLightboxActionRequest[];
}
export interface MediaLightboxGalleryRequest {
    readonly images: readonly MediaLightboxRequest[];
    /** Index of the image that was clicked; clamped into range. */
    readonly index?: number;
}
export interface MediaLightboxActionRequest {
    readonly id: string;
    readonly label: string;
    readonly tooltip?: string | null;
    readonly run: () => void | Promise<void>;
}
export interface MediaLightboxAction extends MediaLightboxActionRequest {
    readonly tooltip: string;
}
export interface MediaLightboxImage {
    readonly src: string;
    readonly alt: string;
    readonly actions: readonly MediaLightboxAction[];
}
/** Backwards-compatible shape for the current image. */
export type MediaLightboxState = MediaLightboxImage;
export declare class MediaLightboxService {
    private readonly images;
    private readonly cursor;
    constructor();
    /** The image currently shown, or null when the lightbox is closed. */
    readonly active: import("@angular/core").Signal<MediaLightboxImage | null>;
    /** Total images in the open gallery (0 when closed). */
    readonly count: import("@angular/core").Signal<number>;
    /** 1-based position of the current image (0 when closed). */
    readonly position: import("@angular/core").Signal<number>;
    readonly hasPrev: import("@angular/core").Signal<boolean>;
    readonly hasNext: import("@angular/core").Signal<boolean>;
    /**
     * Source of the previous / next image, or null at the ends (or closed).
     * Exposed so the lightbox component can warm the browser cache for the
     * neighbours and avoid a flash/reflow when the user pages with arrows.
     */
    readonly prevSrc: import("@angular/core").Signal<string | null>;
    readonly nextSrc: import("@angular/core").Signal<string | null>;
    open(req: MediaLightboxRequest): void;
    openGallery(req: MediaLightboxGalleryRequest): void;
    next(): void;
    prev(): void;
    close(): void;
}
```

## services/modal-stack.service.d.ts

```ts
import { DestroyRef } from '@angular/core';
/**
 * Modal-stack arbitration for Escape.
 *
 * Every modal, full-screen overlay, lightbox, and the detail view registers
 * itself with this service while open. A single document-level Escape
 * listener walks the stack from the top (last pushed wins) and invokes
 * the topmost entry's close handler. Lower entries are not touched.
 *
 * Why this exists: before this service every overlay carried its own
 * `@HostListener('document:keydown.escape')`. With two overlays open at
 * once (e.g. "Add Task" on top of "Task Detail") both handlers fired and
 * the lower surface closed under the user. See task
 * `escape-sollte-modals-schliessen`.
 *
 * Contract:
 * - `push(handler)` returns a disposer; the handler stays on the stack
 *   until the disposer is called. Components typically push when they
 *   open and dispose when they close (effect over the `open` signal).
 * - Strict LIFO: the most recently pushed handler is the one Escape
 *   closes. There is no priority field; insertion order is the model.
 * - The close handler returns `boolean | void`. Returning `false`
 *   *declines* the keystroke: the service does NOT stop propagation,
 *   leaving local template bindings (e.g. an inline title-edit
 *   `(keydown.escape)`) free to handle the same Escape. Returning
 *   `true` or `void` is the normal modal-close path: the service
 *   calls `preventDefault` and `stopImmediatePropagation` so legacy
 *   `document:keydown.escape` listeners that have not migrated do not
 *   double-fire on the same keystroke.
 */
export declare class ModalStackService {
    private readonly entries;
    private readonly inertBefore;
    /** True when at least one modal/overlay is currently on the stack. */
    readonly hasOpen: import("@angular/core").Signal<boolean>;
    /** Depth (number of stacked entries). Useful for tests and diagnostics. */
    readonly depth: import("@angular/core").Signal<number>;
    /** Stable id of the entry currently on top, or null when the stack is empty. */
    readonly topId: import("@angular/core").Signal<string | null>;
    private readonly abortController;
    constructor();
    /**
     * Register a close handler on the modal stack. The returned disposer
     * removes the entry; idempotent. Components should call this when they
     * open and the disposer when they close (or via an Angular `effect`).
     *
     * `id` is informational (test ids, telemetry) and never used for
     * ordering — LIFO is the ordering rule.
     *
     * `close` returns `boolean | void`. Returning `false` declines the
     * keystroke (propagation is allowed to continue so local template
     * bindings on the same Escape can still fire). Any other return value
     * consumes the keystroke.
     */
    push(id: string, close: () => boolean | void, scope?: ModalScope | OverlayScope): () => void;
    /**
     * Convenience: bind a modal entry's lifetime to the owning component
     * via `DestroyRef`. The disposer is called automatically when the
     * component is destroyed, so a forgotten dispose in a teardown branch
     * cannot leave a phantom entry on the stack.
     */
    pushUntilDestroyed(id: string, close: () => boolean | void, destroyRef: DestroyRef, scope?: ModalScope | OverlayScope): () => void;
    /** Test-only: drains the stack. Production code should never need this. */
    clearForTest(): void;
    /** Call after a conditional dialog has rendered so its background becomes inert. */
    refreshModalScope(): void;
    private activeModal;
    private overlayRootsAboveModal;
    private readonly onFocusIn;
    private focusableControls;
    private focusBoundary;
    private readonly onKeydown;
}
/**
 * Helper to bind a signal-typed `open` flag to a modal-stack entry. The
 * `effect` registers when the signal turns truthy and disposes when it turns
 * falsy or the host component is destroyed. Call it in an injection context,
 * typically a constructor. Use it for a surface that renders above another
 * stack entry (the task detail, a dialog): without its own entry, Escape
 * closes the surface underneath and removes the opener with it.
 *
 * Usage in a component:
 * ```
 * constructor() {
 *   bindOpenToModalStack('create-job', this.open, () => this.close());
 * }
 * ```
 */
export declare function bindOpenToModalStack(id: string, open: () => unknown, close: () => boolean | void): void;
/** A true modal's focus surface and the containing subtree kept interactive. */
export interface ModalScope {
    dialog: () => HTMLElement | null | undefined;
    backgroundRoot: () => HTMLElement | null | undefined;
}
/** A floating child above a modal that remains interactive without releasing the page. */
export interface OverlayScope {
    interactiveRoot: () => HTMLElement | null | undefined;
}
```

## services/notification.service.d.ts

```ts
import { NotificationOptions, NotificationState } from '../models/app-dialog.model';
/**
 * Stack-based notification surface. Renders styled toasts (success / info /
 * warning / error) via `<app-notification-stack>` mounted at the app shell.
 *
 * Use for non-blocking outcomes ("Lane cleared", "Task deleted", "Settings
 * saved"). For full error overlays with stack trace + copy, keep using
 * `ErrorDialogService.show` — both surfaces share the same visual language.
 */
export declare class NotificationService {
    private renderPendingFeedback;
    readonly notifications: import("@angular/core").WritableSignal<NotificationState[]>;
    private nextId;
    private timers;
    constructor();
    notify(options: NotificationOptions): number;
    /** Update a live toast in place without changing its dismissal timer. */
    update(id: number, patch: Partial<NotificationOptions>): boolean;
    dismissTopmost(): void;
    /**
     * AGT-3830: dismisses the newest notice that stays until closed and returns
     * true, or returns false when none is open. A surface that owns Escape (the
     * task detail) calls this first, so Escape closes a sticky notice before it
     * closes the surface under it.
     */
    dismissNewestPersistent(): boolean;
    success(message: string, title?: string): number;
    info(message: string, title?: string): number;
    warning(message: string, title?: string): number;
    error(message: string, title?: string): number;
    dismiss(id: number): void;
    dismissAll(): void;
    /** Register the single stack renderer so pending-request toasts paint now. */
    registerRenderer(render: () => void): () => void;
}
```

## services/overlay-portal.service.d.ts

```ts
export interface OverlayPortalRef {
    readonly element: HTMLElement;
    dispose(): void;
}
export type OverlayPortalLayer = 'panel' | 'modal';
export type OverlayPlacement = 'above' | 'below' | 'right' | 'left';
export type OverlayAlignment = 'start' | 'end';
export interface ConnectedOverlayOptions {
    preferredPlacement?: OverlayPlacement;
    alignment?: OverlayAlignment;
    gap?: number;
    viewportPadding?: number;
    minWidth?: number | null;
}
export interface ConnectedOverlayPosition {
    readonly left: number;
    readonly top: number;
    readonly placement: OverlayPlacement;
}
export interface ConnectedOverlayPositionRef {
    update(): void;
    dispose(): void;
}
/**
 * Central append-to-body primitive for floating UI.
 *
 * Components keep their Angular templates and state, but the actual overlay
 * element is hoisted to <body> so it escapes ancestor overflow, transforms,
 * paint containment, and sibling stacking contexts.
 *
 * Connected popovers / menus also use this service for their anchor
 * positioning. That keeps flip and viewport-clamp behaviour in one place
 * instead of re-implementing near-identical `getBoundingClientRect()` math in
 * every picker.
 */
export declare class OverlayPortalService {
    private root;
    attachLayer(element: HTMLElement, layer?: OverlayPortalLayer): OverlayPortalRef;
    attachPanel(element: HTMLElement): OverlayPortalRef;
    attachModal(element: HTMLElement): OverlayPortalRef;
    attach(element: HTMLElement, layerClass?: string, layerZIndex?: string): OverlayPortalRef;
    private overlayRoot;
    positionConnected(anchor: HTMLElement, panel: HTMLElement, options?: ConnectedOverlayOptions): ConnectedOverlayPosition;
    watchConnectedPosition(anchor: HTMLElement, panel: HTMLElement, options?: ConnectedOverlayOptions, apply?: (position: ConnectedOverlayPosition) => void): ConnectedOverlayPositionRef;
    private alignedLeft;
    private placementOrder;
    private clamp;
}
```

## utils/accessible-name.d.ts

```ts
/**
 * Accessible name for a control whose `aria-label` adds context to its
 * visible text. WCAG 2.5.3 (label in name) requires the name to contain the
 * visible text; starting with it lets speech-input users say what they see
 * while screen readers still hear the detail.
 */
export declare function nameWithVisibleText(visible: string, detail?: string | null): string;
```
