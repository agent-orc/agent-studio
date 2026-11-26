/**
 * The part a column plays in every standard grid. The role fixes the column's
 * place on a wide screen and its line in the stacked layout of a narrow one,
 * so grids on one page line up and read in the same order:
 *
 *   identity -> summary -> state -> meta -> actions
 */
export type GridColumnRole = 'identity' | 'summary' | 'state' | 'meta' | 'actions';

export interface GridColumn {
  /** Stable key; a cell template is registered for it with `appGridCell`. Without a template the row's property of this name is shown. */
  key: string;
  /** Identity columns only: the row property shown as a second, quieter line (a username, an id, a description). */
  subKey?: string;
  /** Column heading. An actions column keeps it for assistive technology only. */
  header: string;
  role: GridColumnRole;
  /** CSS width of the column on a wide screen, such as `22%` or `9rem`. Unset columns share the rest. */
  width?: string;
  /** Right-aligned, tabular figures. */
  numeric?: boolean;
}
