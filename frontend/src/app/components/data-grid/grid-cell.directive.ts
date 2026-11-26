import { Directive, TemplateRef, inject, input } from '@angular/core';

/**
 * Registers the template of one column of `<app-data-grid>`. `appGridCellOf`
 * takes the grid's rows and exists only to type the template's row:
 *
 *   <ng-template appGridCell="state" [appGridCellOf]="rows()" let-row>{{ row.status }}</ng-template>
 */
@Directive({ selector: 'ng-template[appGridCell]', standalone: true })
export class GridCellDirective<T = unknown> {
  readonly key = input.required<string>({ alias: 'appGridCell' });
  /** Typing only: the rows whose element type the template's row takes. */
  readonly appGridCellOf = input<readonly T[] | undefined>(undefined);
  readonly template = inject<TemplateRef<{ $implicit: T }>>(TemplateRef);

  static ngTemplateContextGuard<T>(_directive: GridCellDirective<T>, context: unknown): context is { $implicit: T } {
    return true;
  }
}
