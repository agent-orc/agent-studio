import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { fakeAccessService } from '../../testing/access-fixtures';
import { fakeOperatorAgentsService } from '../../testing/operator-fixtures';
import { AccessOperatorAgentsComponent } from './access-operator-agents.component';

describe('AccessOperatorAgentsComponent', () => {
  let operators: ReturnType<typeof fakeOperatorAgentsService>;
  let access: ReturnType<typeof fakeAccessService>;
  let confirm: { confirm: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<AccessOperatorAgentsComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    operators = fakeOperatorAgentsService();
    access = fakeAccessService();
    confirm = { confirm: vi.fn(() => Promise.resolve(true)) };
    await TestBed.configureTestingModule({
      imports: [AccessOperatorAgentsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OperatorAgentsService, useValue: operators },
        { provide: AccessService, useValue: access },
        { provide: ConfirmDialogService, useValue: confirm },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessOperatorAgentsComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('lists each agent with its sessions, actor, state, and delegating Owner', () => {
    const live = byTestId('access-operator-session-ses_live')?.textContent ?? '';
    expect(live).toContain('Release check');
    expect(live).toContain('operator-agent:opa_ops/session:ses_live');
    expect(live).toContain('Active');
    expect(live).toContain('First Owner');
    expect(byTestId('access-operator-session-ses_old')?.textContent).toContain('Expired');
    expect(byTestId('access-operator-session-renew-ses_live')).not.toBeNull();
    expect(byTestId('access-operator-session-renew-ses_old')).toBeNull();
    const rows = Array.from(element.querySelectorAll('[data-testid="access-operator-sessions-opa_ops"] tbody tr'));
    expect(rows.map(row => row.getAttribute('data-state'))).toEqual(['active', 'expired']);
  });

  it('shows a disabled agent quietly without session controls', () => {
    const retired = byTestId('access-operator-agent-opa_off')?.textContent ?? '';
    expect(retired).toContain('Disabled');
    expect(retired).toContain('Revoked');
    expect(byTestId('access-operator-session-start-opa_off')).toBeNull();
    expect(byTestId('access-operator-session-revoke-ses_gone')).toBeNull();
  });

  it('starts a session with the chosen name and lifetime and shows its token once', async () => {
    const name = byTestId<HTMLInputElement>('access-operator-session-name-opa_ops')!;
    name.value = 'Nightly check';
    name.dispatchEvent(new Event('input'));
    const lifetime = byTestId<HTMLSelectElement>('access-operator-session-lifetime-opa_ops')!;
    expect(lifetime.value).toBe('8');
    lifetime.value = '2';
    lifetime.dispatchEvent(new Event('change'));
    byTestId<HTMLButtonElement>('access-operator-session-start-opa_ops')!.click();
    await settle();
    expect(operators.startSession).toHaveBeenCalledWith('opa_ops', 'Nightly check', 2);
    const issued = byTestId('access-operator-session-issued')?.textContent ?? '';
    expect(issued).toContain(operators.issuedToken);
    expect(issued).toContain('only the Owner can renew it');
    expect(issued).toContain('operator-agent:opa_ops/session:ses_new');

    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    byTestId<HTMLButtonElement>('access-operator-session-copy')!.click();
    await settle();
    expect(writeText).toHaveBeenCalledWith(operators.issuedToken);
    expect(byTestId('access-operator-session-copy')?.textContent).toContain('Copied');

    byTestId<HTMLButtonElement>('access-operator-session-done')!.click();
    fixture.detectChanges();
    expect(byTestId('access-operator-session-issued')).toBeNull();
    expect(element.textContent).not.toContain(operators.issuedToken);
  });

  it('renews with the agent lifetime and revokes an active session', async () => {
    byTestId<HTMLButtonElement>('access-operator-session-renew-ses_live')!.click();
    await settle();
    expect(operators.renewSession).toHaveBeenCalledWith('opa_ops', 'ses_live', 8);
    byTestId<HTMLButtonElement>('access-operator-session-revoke-ses_live')!.click();
    await settle();
    expect(operators.revokeSession).toHaveBeenCalledWith('opa_ops', 'ses_live');
  });

  it('disables an agent only after confirmation', async () => {
    confirm.confirm.mockResolvedValueOnce(false);
    byTestId<HTMLButtonElement>('access-operator-agent-disable-opa_ops')!.click();
    await settle();
    expect(operators.disable).not.toHaveBeenCalled();
    byTestId<HTMLButtonElement>('access-operator-agent-disable-opa_ops')!.click();
    await settle();
    expect(confirm.confirm).toHaveBeenCalledTimes(2);
    expect(operators.disable).toHaveBeenCalledWith('opa_ops');
  });

  it('creates an agent with a trimmed name and description', async () => {
    const create = byTestId<HTMLButtonElement>('access-operator-agent-create')!;
    expect(create.disabled).toBe(true);
    const name = byTestId<HTMLInputElement>('access-operator-agent-name')!;
    name.value = '  release-operator ';
    name.dispatchEvent(new Event('input'));
    const description = byTestId<HTMLInputElement>('access-operator-agent-description')!;
    description.value = 'Checks releases';
    description.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    create.click();
    await settle();
    expect(operators.create).toHaveBeenCalledWith('release-operator', 'Checks releases');
  });

  it('narrows the activity view to a session', () => {
    byTestId<HTMLButtonElement>('access-operator-session-activity-ses_old')!.click();
    expect(operators.filterSession).toHaveBeenCalledWith('ses_old');
  });

  it('forgets an issued token when access is denied', async () => {
    byTestId<HTMLButtonElement>('access-operator-session-start-opa_ops')!.click();
    await settle();
    expect(byTestId('access-operator-session-issued')).not.toBeNull();
    access.state.set('denied');
    await settle();
    expect(byTestId('access-operator-session-issued')).toBeNull();
  });

  it('invites the Owner to create the first agent', async () => {
    operators.agents.set([]);
    await settle();
    expect(byTestId('access-operator-agents-empty')).not.toBeNull();
  });
});
