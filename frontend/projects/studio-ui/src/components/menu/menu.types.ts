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
export type MenuItem =
  | MenuRow
  | { kind: 'separator' }
  | { kind: 'header'; label: string };

export interface MenuRow {
  kind: 'row';
  id: string;
  label: string;
  hint?: string;
  disabled?: boolean;
  danger?: boolean;
  active?: boolean;
  tooltip?: string;
  leadingGlyph?: { background: string; initial: string };
  trailingBadge?: string;
}

export interface MenuItemClickEvent {
  id: string;
  item: MenuRow;
}

export type MenuPlacement = 'below' | 'above' | 'right';
