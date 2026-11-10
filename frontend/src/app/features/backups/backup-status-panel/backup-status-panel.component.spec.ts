import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BackupStatusPanelComponent } from './backup-status-panel.component';
import { formatAge, formatBytes, type BackupOverview } from '../backup-status.model';

function overview(patch: Partial<BackupOverview> = {}): BackupOverview {
  return {
    state: 'ok', configured: true, running: false, keyId: 'a1b2c3d4e5f60718', secretKeyId: '0011223344556677',
    intervalHours: 6, verifyIntervalHours: 24, staleAfterHours: 12, retentionCount: 12,
    nextBackupAt: '2026-11-09T12:00:00+00:00',
    lastGood: {
      id: 'studio-backup-20261109T060000Z-abc123', createdAt: '2026-11-09T06:00:00+00:00', sizeBytes: 3 * 1024 * 1024,
      sha256: 'f'.repeat(64), fileCount: 420, verification: 'verified', offHost: 'uploaded', unsettledFiles: 0,
    },
    lastGoodAgeHours: 2.5, stale: false,
    lastRun: { at: '2026-11-09T06:00:00+00:00', trigger: 'schedule', outcome: 'succeeded',
      backupId: 'studio-backup-20261109T060000Z-abc123', error: null, durationMilliseconds: 4200 },
    lastVerification: { at: '2026-11-09T06:05:00+00:00', backupId: 'studio-backup-20261109T060000Z-abc123',
      outcome: 'verified', detail: null },
    offHost: { kind: 'sftp', target: 'sftp://studio@backup.example:22/backups', state: 'current',
      lastUploadedId: 'studio-backup-20261109T060000Z-abc123', lastUploadedAt: '2026-11-09T06:01:00+00:00', lastError: null },
    warnings: [],
    backups: [],
    generatedAt: '2026-11-09T08:30:00+00:00',
    ...patch,
  };
}

describe('BackupStatusPanelComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BackupStatusPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  async function render(body: BackupOverview | null, status = 200) {
    const fixture = TestBed.createComponent(BackupStatusPanelComponent);
    fixture.detectChanges();
    const request = TestBed.inject(HttpTestingController).expectOne('/api/v1/management/backups');
    if (status === 200) request.flush(body);
    else request.flush({ error: 'denied' }, { status, statusText: 'denied' });
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the last good backup with age, size, verification and the off-host copy', async () => {
    const host = await render(overview());

    expect(host.querySelector('[data-testid="operations-backups-state"]')?.getAttribute('data-state')).toBe('ok');
    expect(host.textContent).toContain('Backups current');
    expect(host.querySelector('[data-testid="operations-backups-age"]')?.textContent).toContain('2.5 h');
    expect(host.querySelector('[data-testid="operations-backups-size"]')?.textContent).toContain('3.0 MB');
    expect(host.querySelector('[data-testid="operations-backups-verification"]')?.textContent).toContain('Verified');
    expect(host.querySelector('[data-testid="operations-backups-offhost"]')?.textContent).toContain('Copied');
    expect(host.querySelector('[data-testid="operations-backups-offhost"]')?.textContent).toContain('sftp://studio@backup.example');
    expect(host.querySelector('[data-testid="operations-backups-warnings"]')).toBeNull();
  });

  it('renders the stale warning when the newest good backup is older than the threshold', async () => {
    const host = await render(overview({
      state: 'failing', stale: true, lastGoodAgeHours: 30,
      warnings: [{ code: 'backup-stale', severity: 'danger', message: 'The newest good backup is 30 hours old; the threshold is 12 hours.' }],
    }));

    const warning = host.querySelector('[data-testid="operations-backups-warnings"] [data-code="backup-stale"]');
    expect(warning?.getAttribute('data-severity')).toBe('danger');
    expect(warning?.textContent).toContain('30 hours old');
    expect(host.querySelector('[data-testid="operations-backups-age"]')?.getAttribute('data-stale')).toBe('true');
    expect(host.textContent).toContain('Backups failing');
  });

  it('says backups are off when no key is configured', async () => {
    const host = await render(overview({
      state: 'not-configured', configured: false, lastGood: null, lastGoodAgeHours: null, lastVerification: null,
      offHost: { kind: 'none', target: null, state: 'not-configured', lastUploadedId: null, lastUploadedAt: null, lastError: null },
      warnings: [{ code: 'backup-not-configured', severity: 'warning', message: 'Backups are off: no backup key is configured (Backup:KeyFile). See the backup runbook.' }],
    }));

    expect(host.textContent).toContain('Backups off');
    expect(host.querySelector('[data-testid="operations-backups-last-good"]')?.textContent).toContain('None');
    expect(host.querySelector('[data-testid="operations-backups-verification"]')?.textContent).toContain('Not run yet');
  });

  it('hides itself for accounts without the management role', async () => {
    const host = await render(null, 403);

    expect(host.querySelector('[data-testid="operations-backups"]')).toBeNull();
  });

  it('hides itself when the answer is not a backup overview', async () => {
    const host = await render([] as unknown as BackupOverview);

    expect(host.querySelector('[data-testid="operations-backups"]')).toBeNull();
  });

  it('lists recent backups by stable id', async () => {
    const item = overview().lastGood!;
    const host = await render(overview({ backups: [item, { ...item, id: 'older', verification: 'unverified', offHost: 'failed' }] }));

    const rows = host.querySelectorAll('[data-testid="operations-backups-list"] tbody tr');
    expect([...rows].map(row => row.getAttribute('data-backup-id'))).toEqual([item.id, 'older']);
    expect(rows[1].textContent).toContain('Not verified');
    expect(rows[1].textContent).toContain('Failed');
  });

  it('shows the restore receipt with the archive time after which work is gone (AGT-3734)', async () => {
    const host = await render(overview({
      lastGood: null, lastGoodAgeHours: null, lastVerification: null,
      lastRestore: {
        backupId: 'studio-backup-20261109T060000Z-abc123', archiveFileName: 'studio-backup-20261109T060000Z-abc123.tar.gz.sbk',
        archiveSha256: 'e'.repeat(64), archiveCreatedAt: '2026-11-09T06:00:00+00:00', archiveHost: 'host-a',
        archiveStudioVersion: '1.0.0', restoredAt: '2026-11-10T09:00:00+00:00', restoredBy: 'owner@host-b',
        keyId: 'a1b2c3d4e5f60718', secretKeyId: '0011223344556677', secretKeyCopyId: '0011223344556677',
        filesRestored: 420, checkoutsCloned: 3, skippedSources: [],
      },
    }));

    const receipt = host.querySelector('[data-testid="operations-backups-restore"]');
    expect(receipt?.textContent).toContain('owner@host-b');
    expect(receipt?.textContent).toContain('studio-backup-20261109T060000Z-abc123');
    expect(receipt?.textContent).toContain('host-a');
    expect(receipt?.textContent).toContain('a1b2c3d4e5f60718');
    expect(host.querySelector('[data-testid="operations-backups-restore-loss"]')?.textContent).toContain('is not in this installation');
  });

  it('lists off-host archives the local catalog does not know, or says it cannot list (AGT-3734)', async () => {
    const listed = await render(overview({
      offHost: { ...overview().offHost, kind: 'directory', listing: {
        state: 'listed', detail: null, archiveCount: 4,
        notInCatalog: [{ id: 'studio-backup-20261108T000000Z-111111', fileName: 'studio-backup-20261108T000000Z-111111.tar.gz.sbk',
          sizeBytes: 2048, modifiedAt: '2026-11-08T00:01:00+00:00' }],
      } },
    }));
    expect(listed.querySelector('[data-testid="operations-backups-offhost-listing"]')?.textContent)
      .toContain('1 of 4 archives that this host does not know');
    const rows = listed.querySelectorAll('[data-testid="operations-backups-offhost-unknown"] tbody tr');
    expect([...rows].map(row => row.getAttribute('data-backup-id'))).toEqual(['studio-backup-20261108T000000Z-111111']);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [BackupStatusPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const writeOnly = await render(overview({
      offHost: { ...overview().offHost, listing: {
        state: 'not-listable', detail: 'This host may only write to the sftp target, so it cannot list the archives there.',
        archiveCount: 0, notInCatalog: [],
      } },
    }));
    expect(writeOnly.querySelector('[data-testid="operations-backups-offhost-listing"]')?.getAttribute('data-listing-state')).toBe('not-listable');
    expect(writeOnly.querySelector('[data-testid="operations-backups-offhost-listing-detail"]')?.textContent).toContain('cannot list');
    expect(writeOnly.querySelector('[data-testid="operations-backups-offhost-unknown"]')).toBeNull();
  });

  it('states what the last verification proved and what it did not (AGT-3734)', async () => {
    const host = await render(overview({
      lastVerification: { ...overview().lastVerification!,
        proved: ['The backup key on this host (fingerprint a1b2c3d4e5f60718) opens and authenticates it.'],
        notProved: ['Project checkouts were not cloned from their origins.'] },
    }));

    expect(host.querySelector('[data-testid="operations-backups-verification-proved"]')?.textContent).toContain('key on this host');
    expect(host.querySelector('[data-testid="operations-backups-verification-not-proved"]')?.textContent).toContain('not cloned');
  });

  it('formats ages and sizes', () => {
    expect(formatAge(0.5)).toBe('30 min');
    expect(formatAge(5.55)).toBe('5.6 h');
    expect(formatAge(72)).toBe('3 days');
    expect(formatAge(null)).toBe('None');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
  });
});
