import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { BackupActivityService } from '../../services/backup-activity.service';
import { backupActivity, fakeBackupActivityService } from '../../testing/backup-fixtures';
import { AccessBackupActivityComponent } from './access-backup-activity.component';

describe('AccessBackupActivityComponent', () => {
  let backups: ReturnType<typeof fakeBackupActivityService>;
  let fixture: ComponentFixture<AccessBackupActivityComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    backups = fakeBackupActivityService();
    await TestBed.configureTestingModule({
      imports: [AccessBackupActivityComponent],
      providers: [provideZonelessChangeDetection(), { provide: BackupActivityService, useValue: backups }],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessBackupActivityComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  const byTestId = (id: string) => element.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const cells = (sequence: number) =>
    Array.from(byTestId(`access-backup-entry-${sequence}`)!.querySelectorAll('td')).map(cell => cell.textContent?.replace(/\s+/g, ' ').trim());

  it('lists each record with its operation, actor, backup, outcome, and summary', () => {
    expect(cells(12)).toEqual([
      '12', '9 Nov 2026, 06:00:00 UTC', 'Createstudio-backup-20261109T060000Z-a1b2c3', 'schedule', 'Succeeded',
      'Created encrypted backup studio-backup-20261109T060000Z-a1b2c3 (412 files, 1048576 bytes).',
    ]);
    expect(cells(14).slice(2, 5)).toEqual(['Retention', 'usr_operator', 'Refused']);
    expect(byTestId('access-backup-outcome-14')?.dataset['tone']).toBe('warning');
    expect(byTestId('access-backup-outcome-13')?.dataset['tone']).toBe('danger');
    expect(byTestId('access-backup-outcome-12')?.dataset['tone']).toBe('success');
    expect(byTestId('access-backup-activity-caption')?.textContent).toContain('Newest 3 records');
  });

  it('names the Owner an operator session acted for, and says when nothing happened yet', async () => {
    backups.activity.set(backupActivity([]));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(byTestId('access-backup-entries-empty')).toBeTruthy();
    expect(byTestId('access-backup-activity-caption')?.textContent).toContain('Newest 0 records');

    backups.activity.set(backupActivity([{ ...backupActivity().entries[2], sequence: 20,
      actor: 'operator-agent:opa_ops/session:ses_live', session: 'ses_live', delegatedBy: 'usr_owner', operation: 'backup.verify' }]));
    await fixture.whenStable();
    fixture.detectChanges();
    const actor = byTestId('access-backup-entry-20')!.querySelectorAll('td')[3];
    expect(actor.querySelector('code')?.textContent).toBe('operator-agent:opa_ops/session:ses_live');
    expect(actor.querySelector('span')?.textContent).toBe('for usr_owner');
    expect(cells(20)[2]).toContain('Restore verification');
  });
});
