import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, TemplateRef, computed, contentChildren, input, output } from '@angular/core';
import { GridCellDirective } from './grid-cell.directive';
import type { GridColumn } from './data-grid.types';

/**
 * The standard grid: one header, rows of one height, columns placed by role
 * (identity, summary, state, meta, actions). A selectable grid opens the
 * detail of a row from its identity cell, whose hit area covers the whole row;
 * the grid itself never nests content under a row. Below 760 px of its own width the rows stack: identity, state
 * and actions on the first line, summary and meta below.
 *
 *   <app-data-grid label="People" [columns]="columns" [rows]="people()" [rowId]="personId"
 *                  [selectable]="true" [selectedId]="selected()" (rowSelect)="open($event)">
 *     <ng-template appGridCell="state" [appGridCellOf]="people()" let-row>{{ row.status }}</ng-template>
 *     <app-empty-state gridEmpty title="No people yet" body="..." />
 *   </app-data-grid>
 */
@Component({
  selector: 'app-data-grid',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './data-grid.component.html',
  styleUrl: './data-grid.component.scss',
})
export class DataGridComponent<T> {
  readonly columns = input.required<readonly GridColumn[]>();
  readonly rows = input.required<readonly T[]>();
  /** Stable identity of a row; also the value compared with `selectedId`. */
  readonly rowId = input.required<(row: T) => string>();
  /** Accessible name of the grid. */
  readonly label = input.required<string>();
  readonly selectable = input(false);
  readonly selectedId = input<string | null>(null);
  /** Names the row in the accessible label of its open-details button. */
  readonly rowLabel = input<(row: T) => string>(() => '');
  readonly testId = input<string | null>(null);
  readonly rowTestId = input<(row: T) => string | null>(() => null);
  /** Quiet rows: history and settled entries render dimmed. */
  readonly rowMuted = input<(row: T) => boolean>(() => false);

  readonly rowSelect = output<T>();

  private readonly cells = contentChildren(GridCellDirective);
  private readonly templates = computed(() => new Map(this.cells().map(cell => [cell.key(), cell.template])));

  template(key: string): TemplateRef<{ $implicit: T }> | null {
    return this.templates().get(key) ?? null;
  }

  text(row: T, key: string | undefined): string {
    if (!key) return '';
    const value = (row as Record<string, unknown>)[key];
    return value === null || value === undefined ? '' : String(value);
  }
}
