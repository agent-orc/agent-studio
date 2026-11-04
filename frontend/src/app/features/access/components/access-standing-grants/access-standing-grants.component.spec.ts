import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { StandingGrantsService } from '../../services/standing-grants.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { fakeStandingGrantsService, standingGrant } from '../../testing/grant-fixtures';
import { fakeOperatorAgentsService } from '../../testing/operator-fixtures';
import { AccessStandingGrantsComponent } from './access-standing-grants.component';

describe('AccessStandingGrantsComponent', () => {
  let grants: ReturnType<typeof fakeStandingGrantsService>;
  let confirm: { confirm: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<AccessStandingGrantsComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    grants = fakeStandingGrantsService([
      standingGrant({ id: 'sgr_old', state: 'revoked', revokedAt: '2026-11-01T10:00:00Z', revokedBy: 'usr_owner', issuedAt: '2026-10-30T08:00:00Z' }),
      standingGrant(),
    ]);
    confirm = { confirm: vi.fn(() => Promise.resolve(true)) };
    await TestBed.configureTestingModule({
      imports: [AccessStandingGrantsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: StandingGrantsService, useValue: grants },
        { provide: OperatorAgentsService, useValue: fakeOperatorAgentsService() },
        { provide: AccessService, useValue: fakeAccessService() },
        { provide: ConfirmDialogService, useValue: confirm },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessStandingGrantsComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;

  function type(id: string, value: string): void {
    const input = byTestId<HTMLInputElement>(id)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function check(id: string): void {
    const box = byTestId<HTMLInputElement>(id)!;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function choose(id: string, value: string): void {
    const select = byTestId<HTMLSelectElement>(id)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('lists the catalogue by tier and only T1 operations as grantable', () => {
    expect(byTestId('access-catalogue-T0')?.textContent).toContain('T0 observe');
    expect(byTestId('access-catalogue-T2')?.textContent).toContain('Change secrets');
    expect(byTestId('access-grant-operation-card.requeue')).not.toBeNull();
    expect(byTestId('access-grant-operation-secrets.change')).toBeNull();
    expect(byTestId('access-grant-operation-observe')).toBeNull();
  });

  it('shows live grants first with rate use, scope, and state, and controls only for live ones', () => {
    const rows = Array.from(element.querySelectorAll('[data-testid="access-grants-table"] tbody tr'));
    expect(rows.map(row => row.getAttribute('data-state'))).toEqual(['active', 'revoked']);
    const live = byTestId('access-grant-sgr_live')?.textContent ?? '';
    expect(live).toContain('Requeue a card, Steer a card');
    expect(live).toContain('Project PROJ-001');
    expect(live).toContain('3 / 10 per hour');
    expect(live).toContain('Any time');
    expect(byTestId('access-grant-renew-sgr_live')).not.toBeNull();
    expect(byTestId('access-grant-revoke-sgr_old')).toBeNull();
  });

  it('issues a grant with a fresh second factor code', async () => {
    const issue = byTestId<HTMLButtonElement>('access-grant-issue')!;
    expect(issue.disabled).toBe(true);
    check('access-grant-operation-card.requeue');
    type('access-grant-project', 'PROJ-001');
    type('access-grant-rate', '4');
    type('access-grant-window-start', '22:00');
    type('access-grant-window-end', '06:00');
    choose('access-grant-lifetime', '14');
    type('access-grant-reason', 'Requeue stalled cards overnight');
    type('access-grant-code', '123456');
    expect(issue.disabled).toBe(false);
    issue.click();
    await fixture.whenStable();
    expect(grants.issue).toHaveBeenCalledWith({
      principalId: 'opa_ops',
      operations: ['card.requeue'],
      scope: { kind: 'project', project: 'PROJ-001' },
      ratePerHour: 4,
      window: { startUtc: '22:00', endUtc: '06:00' },
      expiresInDays: 14,
      reason: 'Requeue stalled cards overnight',
    }, { code: '123456' });
  });

  it('refuses a scope that cannot cover a selected operation before asking the server', () => {
    check('access-grant-operation-service.restart');
    type('access-grant-project', 'PROJ-001');
    type('access-grant-reason', 'Restart the indexer');
    expect(byTestId('access-grant-uncovered')?.textContent).toContain('Restart an allowlisted service');
    expect(byTestId<HTMLButtonElement>('access-grant-issue')!.disabled).toBe(true);
    choose('access-grant-scope-kind', 'host');
    type('access-grant-host', 'build-host');
    expect(byTestId('access-grant-uncovered')).toBeNull();
    expect(byTestId<HTMLButtonElement>('access-grant-issue')!.disabled).toBe(false);
  });

  it('renews with a passkey when no code is entered and revokes after confirmation', async () => {
    byTestId<HTMLButtonElement>('access-grant-renew-sgr_live')!.click();
    await fixture.whenStable();
    expect(grants.renew).toHaveBeenCalledWith('sgr_live', 7, { passkey: true });

    byTestId<HTMLButtonElement>('access-grant-revoke-sgr_live')!.click();
    await fixture.whenStable();
    expect(confirm.confirm).toHaveBeenCalled();
    expect(grants.revoke).toHaveBeenCalledWith('sgr_live', '');
  });
});
