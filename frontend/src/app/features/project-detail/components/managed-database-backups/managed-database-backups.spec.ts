import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ManagedDatabaseBackupsComponent } from './managed-database-backups';
import type { DatabaseOperationRequest, ManagedDatabase } from '../managed-databases-panel/managed-database.model';

describe('ManagedDatabaseBackupsComponent', () => {
  const database = (overrides: Partial<ManagedDatabase> = {}): ManagedDatabase => ({
    environmentId: 'stage', name: 'orders', engine: 'postgres', version: '16.6', runningVersion: '16.6', size: 'small',
    resources: { cpus: 0.5, memoryMb: 512, pidsLimit: 256 }, variable: 'DATABASE_URL', credentialReference: 'secret://demo/stage/X',
    state: 'running', declared: true, volumeBytes: 1024, observedAt: null, message: null,
    deletionRequestedBy: null, deletionRequestedAt: null, deletedAt: null, ready: true, connections: 1, maxConnections: 100,
    backupPolicy: { intervalHours: 24, retentionDays: 30, minimumKept: 7 }, volumeAlertBytes: 1024 * 1024, lastBackupAt: null,
    backupStatus: 'none', lastBackupError: null, lastBackupAttemptAt: null, environmentKind: 'stage', operations: [],
    backups: [{ id: 'b1', takenAt: '2026-11-16T02:00:00Z', bytes: 2048, kind: 'pre-restore', verifiedAt: null, verifiedTables: null }],
    ...overrides,
  });

  async function show(value: ManagedDatabase, targets: string[] = []) {
    await TestBed.configureTestingModule({
      imports: [ManagedDatabaseBackupsComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ManagedDatabaseBackupsComponent);
    fixture.componentRef.setInput('database', value);
    fixture.componentRef.setInput('targets', targets);
    const requests: DatabaseOperationRequest[] = [];
    fixture.componentInstance.operate.subscribe(request => requests.push(request));
    fixture.detectChanges();
    return { fixture, requests, element: fixture.nativeElement as HTMLElement };
  }

  it('labels a backup taken before a restore and shows it as not verified', async () => {
    const { element } = await show(database());
    const row = element.querySelector('[data-testid="managed-database-backup-b1"]')!;
    expect(row.textContent).toContain('Before restore');
    expect(row.textContent).toContain('Not verified');
    expect(element.querySelector('[data-testid="managed-database-last-backup"]')?.textContent?.trim()).toBe('No backup yet');
    expect(element.querySelector('[data-testid="managed-database-backup-status"]')?.textContent).toContain('No backups expected');
  });

  it('offers only other running databases as restore targets when this one is not running', async () => {
    const { fixture, element } = await show(database({ state: 'retained' }), ['orders_new']);
    expect(element.querySelector('[data-testid="managed-database-back-up-now"]')).toBeNull();
    (element.querySelector('[data-testid="managed-database-backup-restore"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const targets = Array.from(element.querySelectorAll('input[type="radio"]')).map(item => (item as HTMLInputElement).value);
    expect(targets).toEqual(['orders_new']);
    expect(fixture.componentInstance.target()).toBe('orders_new');
  });

  it('hides restore when no target exists and emits the backup request', async () => {
    const { element, requests } = await show(database({ state: 'provisioning' }));
    expect(element.querySelector('[data-testid="managed-database-backup-restore"]')).toBeNull();
    clickTestId(element, 'managed-database-backup-verify');
    expect(requests.map(item => [item.path, item.body])).toEqual([['operations', { kind: 'verify', backupId: 'b1' }]]);
  });

  function clickTestId(element: HTMLElement, testId: string) {
    (element.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
  }
});
