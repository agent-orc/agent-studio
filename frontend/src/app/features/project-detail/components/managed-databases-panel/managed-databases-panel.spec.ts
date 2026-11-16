import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ManagedDatabasesPanelComponent, databaseState, formatBytes, type ManagedDatabase } from './managed-databases-panel';

describe('ManagedDatabasesPanelComponent', () => {
  const list = { environments: [{ declaration: { id: 'stage', name: 'Stage' } }, { declaration: { id: 'production', name: 'Production' } }] };
  const database = (overrides: Partial<ManagedDatabase> = {}): ManagedDatabase => ({
    environmentId: 'stage', name: 'orders', engine: 'postgres', version: '16.6', runningVersion: '16.4', size: 'small',
    resources: { cpus: 0.5, memoryMb: 512, pidsLimit: 256 }, variable: 'DATABASE_URL',
    credentialReference: 'secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD', state: 'running', declared: true,
    volumeBytes: 5 * 1024 * 1024, observedAt: '2026-11-16T09:00:00Z', message: null,
    deletionRequestedBy: null, deletionRequestedAt: null, deletedAt: null, ...overrides,
  });

  async function show(databases: ManagedDatabase[]) {
    await TestBed.configureTestingModule({
      imports: [ManagedDatabasesPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ManagedDatabasesPanelComponent);
    const http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('projectName', 'demo');
    fixture.detectChanges();
    http.expectOne('/api/projects/demo/environments').flush(list);
    http.expectOne('/api/projects/demo/environments/stage/databases').flush({ databases });
    http.expectOne('/api/projects/demo/environments/production/databases').flush({ databases: [] });
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, http, element: fixture.nativeElement as HTMLElement };
  }

  it('shows engine, version, size, state, volume usage and the credential reference per environment', async () => {
    const { element, http } = await show([database()]);

    const row = element.querySelector('[data-testid="managed-database-stage-orders"]')!;
    expect(row.textContent).toContain('PostgreSQL 16.4');
    expect(row.textContent).toContain('(declared 16.6)');
    expect(row.textContent).toContain('small · 0.5 CPU · 512 MB');
    expect(row.textContent).toContain('5.0 MB');
    expect(row.textContent).toContain('Running');
    expect(row.textContent).toContain('DATABASE_URL_FILE');
    expect(row.textContent).toContain('secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD');
    expect(element.querySelector('[data-testid="managed-databases-production"]')).toBeNull();
    expect(row.querySelector('button')).toBeNull();
    http.verify();
  });

  it('deletes a retained database only after the owner types its name', async () => {
    const { fixture, element, http } = await show([database({ state: 'retained', declared: false })]);
    const deleteButton = () => Array.from(element.querySelectorAll('button')).find(item => item.textContent?.includes('Delete data'))!;

    deleteButton().click();
    fixture.detectChanges();
    const confirm = Array.from(element.querySelectorAll('button')).find(item => item.textContent?.includes('permanently'))!;
    expect(confirm.disabled).toBe(true);
    const input = element.querySelector('[data-testid="managed-database-confirm-name"]') as HTMLInputElement;
    input.value = 'order';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    confirm.click();
    http.expectNone('/api/projects/demo/environments/stage/databases/orders/deletion');

    input.value = 'orders';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(confirm.disabled).toBe(false);
    confirm.click();
    const request = http.expectOne('/api/projects/demo/environments/stage/databases/orders/deletion');
    expect(request.request.body).toEqual({ confirm: 'orders' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
    http.expectOne('/api/projects/demo/environments').flush({ environments: [] });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.textContent).toContain('Deletion of orders requested');
    http.verify();
  });

  it('shows the server refusal when the caller is not an owner', async () => {
    const { fixture, element, http } = await show([database({ state: 'retained', declared: false })]);
    fixture.componentInstance.startDeletion(database({ state: 'retained' }));
    fixture.componentInstance.typed.set('orders');
    fixture.componentInstance.confirmDeletion(database({ state: 'retained' }));
    http.expectOne('/api/projects/demo/environments/stage/databases/orders/deletion')
      .flush({ error: 'Deleting database data requires a signed-in Owner.' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('signed-in Owner');
    http.verify();
  });

  it('maps states to calm tones and formats sizes', () => {
    expect(databaseState('retained')).toEqual({ label: 'Retained, not declared', tone: 'neutral' });
    expect(databaseState('upgrade-refused').tone).toBe('warning');
    expect(databaseState('something-new')).toEqual({ label: 'something-new', tone: 'neutral' });
    expect(formatBytes(null)).toBe('Not measured yet');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe('3.0 GB');
  });
});
