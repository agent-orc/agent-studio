import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ManagedDatabasesPanelComponent, databaseState, formatBytes, type ManagedDatabase } from './managed-databases-panel';
import { backupStatus, connections, readiness, type DatabaseBackup, type DatabaseOperation } from './managed-database.model';

describe('ManagedDatabasesPanelComponent', () => {
  const list = { environments: [{ declaration: { id: 'stage', name: 'Stage' } }, { declaration: { id: 'production', name: 'Production' } }] };
  const database = (overrides: Partial<ManagedDatabase> = {}): ManagedDatabase => ({
    environmentId: 'stage', name: 'orders', engine: 'postgres', version: '16.6', runningVersion: '16.4', size: 'small',
    resources: { cpus: 0.5, memoryMb: 512, pidsLimit: 256 }, variable: 'DATABASE_URL',
    credentialReference: 'secret://demo/stage/MANAGED_DB_ORDERS_PASSWORD', state: 'running', declared: true,
    volumeBytes: 5 * 1024 * 1024, observedAt: '2026-11-16T09:00:00Z', message: null,
    deletionRequestedBy: null, deletionRequestedAt: null, deletedAt: null,
    ready: true, connections: 3, maxConnections: 100, backupPolicy: { intervalHours: 24, retentionDays: 30, minimumKept: 7 },
    volumeAlertBytes: 5 * 1024 * 1024 * 1024, lastBackupAt: '2026-11-16T02:00:00Z', backupStatus: 'ok',
    lastBackupError: null, lastBackupAttemptAt: '2026-11-16T02:00:00Z', backups: [], operations: [], environmentKind: 'stage',
    ...overrides,
  });
  const backup = (id: string, overrides: Partial<DatabaseBackup> = {}): DatabaseBackup => ({
    id, takenAt: '2026-11-16T02:00:00Z', bytes: 2 * 1024 * 1024, kind: 'scheduled', verifiedAt: null, verifiedTables: null, ...overrides,
  });
  const operation = (overrides: Partial<DatabaseOperation> = {}): DatabaseOperation => ({
    id: 'op-1', kind: 'restore', backupId: 'b1', sourceDatabase: 'orders', targetDatabase: 'orders', state: 'awaiting-approval',
    requestedBy: 'maria', requestedAt: '2026-11-16T09:00:00Z', approvedBy: null, approvedAt: null, finishedAt: null, message: null,
    ...overrides,
  });
  const operationsUrl = '/api/projects/demo/environments/stage/databases/orders/operations';
  const button = (root: Element, testId: string) => root.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement;
  const typeInto = (root: Element, testId: string, value: string) => {
    const input = root.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };
  async function settle(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }) {
    await fixture.whenStable();
    fixture.detectChanges();
  }
  /** A successful mutation reloads the panel; answer the reload with the same database list. */
  function reload(http: HttpTestingController, databases: ManagedDatabase[]) {
    http.expectOne('/api/projects/demo/environments').flush(list);
    http.expectOne('/api/projects/demo/environments/stage/databases').flush({ databases });
    http.expectOne('/api/projects/demo/environments/production/databases').flush({ databases: [] });
  }

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
    expect(Array.from(row.querySelectorAll('button')).map(item => item.textContent?.trim())).toEqual(['Back up now']);
    http.verify();
  });

  it('shows readiness, connections and the volume against its alert threshold', async () => {
    const { element, http } = await show([
      database(),
      database({ name: 'reports', ready: null, connections: null, volumeBytes: 6 * 1024 * 1024 * 1024 }),
    ]);

    const orders = element.querySelector('[data-testid="managed-database-stage-orders"]')!;
    expect(orders.querySelector('[data-testid="managed-database-ready"]')?.textContent).toContain('Ready');
    expect(orders.querySelector('[data-testid="managed-database-connections"]')?.textContent?.trim()).toBe('3 of 100');
    expect(orders.querySelector('[data-testid="managed-database-volume"]')?.textContent).toContain('5.0 MB of 5.0 GB alert threshold');
    expect(orders.querySelector('[data-testid="managed-database-volume-alert"]')).toBeNull();
    const reports = element.querySelector('[data-testid="managed-database-stage-reports"]')!;
    expect(reports.querySelector('[data-testid="managed-database-ready"]')?.textContent).toContain('Unknown');
    expect(reports.querySelector('[data-testid="managed-database-volume-alert"]')?.textContent).toContain('at or above its 5.0 GB alert threshold');
    expect(readiness(false)).toEqual({ label: 'Not ready', tone: 'danger' });
    expect(connections({ connections: 4, maxConnections: null })).toBe('4');
    http.verify();
  });

  it('shows the last backup, the status, the policy and the last backup error', async () => {
    const { element, http } = await show([
      database(),
      database({ name: 'reports', lastBackupAt: null, backupStatus: 'failed', lastBackupError: 'pg_dump exited with 1.' }),
      database({ name: 'billing', lastBackupAt: null, backupStatus: 'pending' }),
    ]);

    const orders = element.querySelector('[data-testid="managed-database-stage-orders"]')!;
    expect(orders.querySelector('[data-testid="managed-database-last-backup"]')?.textContent?.trim()).toBe('Last backup 16 Nov 2026, 02:00 UTC');
    expect(orders.querySelector('[data-testid="managed-database-backup-status"]')?.textContent).toContain('Backups current');
    expect(orders.querySelector('[data-testid="managed-database-backup-policy"]')?.textContent).toBe('Every 24 h, kept 30 days, at least 7 kept');
    const reports = element.querySelector('[data-testid="managed-database-stage-reports"]')!;
    expect(reports.querySelector('[data-testid="managed-database-last-backup"]')?.textContent?.trim()).toBe('No backup yet');
    expect(reports.querySelector('[data-testid="managed-database-backup-status"]')?.textContent).toContain('Last backup failed');
    expect(reports.querySelector('[data-testid="managed-database-backup-error"]')?.textContent).toContain('pg_dump exited with 1.');
    const billing = element.querySelector('[data-testid="managed-database-stage-billing"]')!;
    expect(billing.querySelector('[data-testid="managed-database-last-backup"]')?.textContent?.trim()).toBe('No backup yet');
    expect(billing.querySelector('[data-testid="managed-database-backup-status"]')?.textContent).toContain('First backup pending');
    expect(backupStatus('ok')).toEqual({ label: 'Backups current', tone: 'success' });
    expect(backupStatus('pending')).toEqual({ label: 'First backup pending', tone: 'info' });
    expect(backupStatus('overdue')).toEqual({ label: 'Backup overdue', tone: 'warning' });
    expect(backupStatus('failed')).toEqual({ label: 'Last backup failed', tone: 'danger' });
    expect(backupStatus('none')).toEqual({ label: 'No backups expected', tone: 'neutral' });
    http.verify();
  });

  it('starts a backup and a test restore and reloads after each', async () => {
    const databases = [database({ backups: [backup('b1', { verifiedAt: '2026-11-16T03:00:00Z', verifiedTables: 12, kind: 'manual' })] })];
    const { fixture, element, http } = await show(databases);
    const row = element.querySelector('[data-testid="managed-database-backup-b1"]')!;
    expect(row.textContent).toContain('16 Nov 2026, 02:00 UTC');
    expect(row.textContent).toContain('Manual');
    expect(row.textContent).toContain('2.0 MB');
    expect(row.textContent).toContain('Verified 16 Nov, 12 tables');

    button(element, 'managed-database-back-up-now').click();
    const backupRequest = http.expectOne(operationsUrl);
    expect(backupRequest.request.method).toBe('POST');
    expect(backupRequest.request.body).toEqual({ kind: 'backup' });
    backupRequest.flush({ operation: operation({ kind: 'backup', state: 'queued' }) }, { status: 202, statusText: 'Accepted' });
    reload(http, databases);
    await settle(fixture);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Backup of orders queued');

    button(element, 'managed-database-backup-verify').click();
    const verifyRequest = http.expectOne(operationsUrl);
    expect(verifyRequest.request.body).toEqual({ kind: 'verify', backupId: 'b1' });
    verifyRequest.flush({ operation: operation({ kind: 'verify', state: 'queued' }) }, { status: 202, statusText: 'Accepted' });
    reload(http, databases);
    await settle(fixture);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Test restore of the backup from 16 Nov 2026, 02:00 UTC queued');
    http.verify();
  });

  it('enables the restore only after the target name is typed and sends target and confirmation', async () => {
    const databases = [
      database({ environmentKind: 'production', backups: [backup('b1')] }),
      database({ name: 'orders_restored', environmentKind: 'production' }),
      database({ name: 'archive', state: 'retained', declared: false, environmentKind: 'production' }),
    ];
    const { fixture, element, http } = await show(databases);
    const orders = element.querySelector('[data-testid="managed-database-stage-orders"]')!;
    button(orders, 'managed-database-backup-restore').click();
    fixture.detectChanges();

    const form = orders.querySelector('[data-testid="managed-database-restore-form"]')!;
    expect(form.textContent).toContain('backed up first');
    expect(form.textContent).toContain('waits for an Owner approval');
    expect(form.querySelector('[data-testid="managed-database-restore-target-archive"]')).toBeNull();
    const submit = button(form, 'managed-database-restore-submit');
    expect(submit.disabled).toBe(true);
    typeInto(form, 'managed-database-restore-confirm', 'orders');
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);

    (form.querySelector('[data-testid="managed-database-restore-target-orders_restored"]') as HTMLInputElement).click();
    fixture.detectChanges();
    expect(form.textContent).toContain('Type orders_restored to replace its data');
    expect(submit.disabled).toBe(true);
    typeInto(form, 'managed-database-restore-confirm', 'orders_restore');
    fixture.detectChanges();
    submit.click();
    http.expectNone(operationsUrl);
    typeInto(form, 'managed-database-restore-confirm', 'orders_restored');
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    submit.click();

    const request = http.expectOne(operationsUrl);
    expect(request.request.body).toEqual({ kind: 'restore', backupId: 'b1', targetDatabase: 'orders_restored', confirm: 'orders_restored' });
    request.flush({ operation: operation({ targetDatabase: 'orders_restored' }) }, { status: 202, statusText: 'Accepted' });
    reload(http, databases);
    await settle(fixture);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Restore into orders_restored waits for an Owner approval');
    expect(element.querySelector('[data-testid="managed-database-restore-form"]')).toBeNull();
    http.verify();
  });

  it('approves and rejects a restore that waits for an owner', async () => {
    const databases = [database({ backups: [backup('b1')], operations: [operation()] })];
    const { fixture, element, http } = await show(databases);
    const row = element.querySelector('[data-testid="managed-database-operation-op-1"]')!;
    expect(row.textContent).toContain('Restore');
    expect(row.textContent).toContain('Awaiting approval');
    expect(row.textContent).toContain('of the backup from 16 Nov 2026, 02:00 UTC into orders');
    expect(row.textContent).toContain('Requested by maria 16 Nov 2026, 09:00 UTC');

    button(row, 'managed-database-operation-approve').click();
    const approve = http.expectOne(`${operationsUrl}/op-1/approve`);
    expect(approve.request.method).toBe('POST');
    expect(approve.request.body).toEqual({});
    approve.flush({ operation: operation({ state: 'queued', approvedBy: 'olga' }) });
    reload(http, databases);
    await settle(fixture);
    expect(element.querySelector('[role="status"]')?.textContent).toContain('Restore into orders approved');

    button(element, 'managed-database-operation-reject').click();
    const reject = http.expectOne(`${operationsUrl}/op-1/reject`);
    expect(reject.request.body).toEqual({});
    reject.flush({ error: 'Approving a production restore requires a signed-in Owner.' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('requires a signed-in Owner');
    expect(element.querySelector('[role="status"]')).toBeNull();
    http.verify();
  });

  it('shows the newest five backups and discloses the rest', async () => {
    const backups = Array.from({ length: 7 }, (_, index) => backup(`b${index + 1}`));
    const { fixture, element, http } = await show([database({ backups })]);
    expect(element.querySelectorAll('[data-testid^="managed-database-backup-b"]').length).toBe(5);
    const toggle = button(element, 'managed-database-backups-toggle');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.textContent).toContain('Show all 7 backups');
    toggle.click();
    fixture.detectChanges();
    expect(element.querySelectorAll('[data-testid^="managed-database-backup-b"]').length).toBe(7);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    http.verify();
  });

  it('deletes a retained database only after the owner types its name', async () => {
    const { fixture, element, http } = await show([database({ state: 'retained', declared: false })]);
    const deleteButton = () => Array.from(element.querySelectorAll('button')).find(item => item.textContent?.includes('Delete data'))!;

    deleteButton().click();
    fixture.detectChanges();
    expect(element.querySelector('[data-testid="managed-database-confirm"]')?.textContent)
      .toContain('Deleting removes the volume and every backup of orders');
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
