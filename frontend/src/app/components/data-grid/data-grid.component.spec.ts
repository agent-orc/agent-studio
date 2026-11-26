import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { DataGridComponent, GridCellDirective, type GridColumn } from './index';

interface Person { id: string; name: string; username: string; status: string; note: string | null }

/* eslint-disable @angular-eslint/component-max-inline-declarations */
@Component({
  standalone: true,
  imports: [DataGridComponent, GridCellDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-data-grid label="People" testId="people" [columns]="columns" [rows]="rows()" [rowId]="rowId" [rowTestId]="rowTestId"
                   [selectable]="true" [selectedId]="selected()" (rowSelect)="selected.set($event.id)">
      <ng-template appGridCell="status" [appGridCellOf]="rows()" let-row><em class="status">{{ row.status }}</em></ng-template>
      <ng-template appGridCell="actions" [appGridCellOf]="rows()" let-row><button type="button" class="more">More for {{ row.name }}</button></ng-template>
      <p gridEmpty class="empty">No people yet.</p>
    </app-data-grid>`,
})
class HostComponent {
  readonly columns: GridColumn[] = [
    { key: 'name', subKey: 'username', header: 'Person', role: 'identity', width: '30%' },
    { key: 'status', header: 'State', role: 'state' },
    { key: 'note', header: 'Note', role: 'meta' },
    { key: 'actions', header: 'Actions', role: 'actions' },
  ];
  readonly rows = signal<readonly Person[]>([
    { id: 'a', name: 'Mara', username: 'mara', status: 'Active', note: 'Owner' },
    { id: 'b', name: 'Jonas', username: 'jonas', status: 'Invited', note: null },
  ]);
  readonly selected = signal<string | null>(null);
  readonly rowId = (row: Person) => row.id;
  readonly rowTestId = (row: Person) => `person-${row.id}`;
}

describe('DataGridComponent', () => {
  function mount() {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('renders one labelled table with a header per column and a role on every cell', () => {
    const { root } = mount();
    const table = root.querySelector('[data-testid="people"]')!;

    expect(table.getAttribute('aria-label')).toBe('People');
    expect([...table.querySelectorAll('th')].map(th => th.textContent!.trim())).toEqual(['Person', 'State', 'Note', 'Actions']);
    expect((table.querySelector('col') as HTMLElement).style.width).toBe('30%');
    const cells = [...table.querySelectorAll('[data-testid="person-a"] td')];
    expect(cells.map(td => td.getAttribute('data-role'))).toEqual(['identity', 'state', 'meta', 'actions']);
    expect(cells.map(td => td.getAttribute('data-label'))).toEqual(['Person', 'State', 'Note', 'Actions']);
  });

  it('shows the property, its second line, a custom template, and a quiet dash for a missing value', () => {
    const { root } = mount();
    const first = root.querySelector('[data-testid="person-a"]')!;
    const second = root.querySelector('[data-testid="person-b"]')!;

    expect(first.querySelector('.grid__line')!.textContent).toBe('Mara');
    expect(first.querySelector('.grid__sub')!.textContent).toBe('mara');
    expect(first.querySelector('.status')!.textContent).toBe('Active');
    expect(first.querySelector('td[data-role="meta"]')!.textContent!.trim()).toBe('Owner');
    expect(second.querySelector('td[data-role="meta"] .grid__none')!.textContent).toBe('–');
  });

  it('selects a row from its named identity button and marks it', () => {
    const { fixture, root } = mount();
    const open = root.querySelector('[data-testid="person-b"] .grid__open') as HTMLButtonElement;
    expect(open.getAttribute('aria-label')).toBe('Open details of Jonas');

    open.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.selected()).toBe('b');
    expect(root.querySelector('[data-testid="person-b"]')!.getAttribute('data-selected')).toBe('true');
    expect(open.getAttribute('aria-current')).toBe('true');
    expect(root.querySelector('[data-testid="person-a"]')!.getAttribute('data-selected')).toBeNull();
  });

  it('leaves row controls outside the identity button, so they do not select the row', () => {
    const { fixture, root } = mount();
    (root.querySelector('[data-testid="person-a"] .more') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.selected()).toBeNull();
  });

  it('shows the projected empty state instead of an empty table', () => {
    const { fixture, root } = mount();
    fixture.componentInstance.rows.set([]);
    fixture.detectChanges();

    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('[data-testid="people-empty"] .empty')!.textContent).toBe('No people yet.');
  });
});
