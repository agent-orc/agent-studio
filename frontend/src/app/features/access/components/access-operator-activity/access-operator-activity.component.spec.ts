import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { fakeOperatorAgentsService, operatorActivity } from '../../testing/operator-fixtures';
import { AccessOperatorActivityComponent } from './access-operator-activity.component';

describe('AccessOperatorActivityComponent', () => {
  let operators: ReturnType<typeof fakeOperatorAgentsService>;
  let fixture: ComponentFixture<AccessOperatorActivityComponent>;
  let element: HTMLElement;

  beforeEach(async () => {
    operators = fakeOperatorAgentsService();
    await TestBed.configureTestingModule({
      imports: [AccessOperatorActivityComponent],
      providers: [provideZonelessChangeDetection(), { provide: OperatorAgentsService, useValue: operators }],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessOperatorActivityComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  });

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;

  async function show(activity = operatorActivity()): Promise<void> {
    operators.activity.set(activity);
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('loads on open and lists every entry with its identity, operation, and short digest', () => {
    expect(operators.loadActivity).toHaveBeenCalledTimes(1);
    const entry = byTestId('access-audit-entry-42')!;
    const cells = Array.from(entry.querySelectorAll('td')).map(cell => cell.textContent?.trim());
    expect(cells[0]).toBe('42');
    expect(cells[1]).toBe('2 Nov 2026, 08:30:00 UTC');
    expect(cells.slice(2)).toEqual([
      'iam', 'operator-agent:opa_ops/session:ses_live', 'ses_live', 'usr_owner', 'task.move', 'PROJ-001/AGT-1', 'succeeded', 'sha256-a1b2c',
    ]);
    const digest = entry.querySelector('code[title]')!;
    expect(digest.getAttribute('title')).toBe(`sha256-${'a1b2c3d4'.repeat(8)}`);
    expect(byTestId('access-audit-entry-41')?.textContent).toContain('–');
    expect(byTestId('access-audit-entries-caption')?.textContent).toContain('Newest 2 entries');
  });

  it('groups sessions per agent and narrows the entries to a clicked session', async () => {
    const sessions = byTestId('access-operator-activity-sessions')!;
    expect(sessions.textContent).toContain('studio-operator');
    expect(sessions.textContent).toContain('retired-operator');
    expect(byTestId('access-activity-session-all')?.getAttribute('aria-current')).toBe('true');
    byTestId<HTMLButtonElement>('access-activity-session-ses_live')!.click();
    expect(operators.filterSession).toHaveBeenCalledWith('ses_live');
    await show(operatorActivity({ entries: [operatorActivity().entries[0]] }));
    expect(byTestId('access-activity-session-ses_live')?.getAttribute('aria-current')).toBe('true');
    expect(byTestId('access-activity-session-all')?.getAttribute('aria-current')).toBeNull();
    expect(byTestId('access-audit-entries-caption')?.textContent).toContain('1 entry of operator-agent:opa_ops/session:ses_live');
    byTestId<HTMLButtonElement>('access-activity-session-all')!.click();
    expect(operators.filterSession).toHaveBeenLastCalledWith(null);
  });

  it('renders a verified chain and a healthy sink quietly', () => {
    const integrity = byTestId('access-audit-integrity')!;
    expect(integrity.textContent).toContain('Chain verified through entry 42');
    expect(integrity.getAttribute('data-tone')).toBeNull();
    expect(integrity.getAttribute('role')).toBeNull();
    const forwarding = byTestId('access-audit-forwarding')!;
    expect(forwarding.textContent).toContain('Forwarded 40 of 42 to syslog');
    expect(forwarding.textContent).toContain('2 pending');
    expect(forwarding.getAttribute('data-tone')).toBeNull();
  });

  it('turns acute only when the server raises an alert', async () => {
    const base = operatorActivity();
    await show({
      ...base,
      integrity: { state: 'failed', checkedAt: '2026-11-02T09:00:00Z', verifiedThrough: 40, message: 'Hash mismatch at entry 41.', alert: true },
      forwarding: { ...base.forwarding, pending: 30, lastError: 'Sink unreachable.', alert: true },
    });
    const integrity = byTestId('access-audit-integrity')!;
    expect(integrity.getAttribute('data-tone')).toBe('alert');
    expect(integrity.getAttribute('role')).toBe('alert');
    expect(integrity.textContent).toContain('Hash mismatch at entry 41.');
    expect(byTestId('access-audit-forwarding')?.getAttribute('data-tone')).toBe('alert');
    expect(byTestId('access-audit-forwarding-error')?.textContent).toContain('Sink unreachable.');
  });

  it('shows a missing sink as a neutral note, not an alarm', async () => {
    const base = operatorActivity();
    await show({ ...base, forwarding: { ...base.forwarding, configured: false, sinkKind: 'none', forwardedSequence: 0, pending: 42, alert: false } });
    const forwarding = byTestId('access-audit-forwarding')!;
    expect(forwarding.textContent).toContain('No off-host sink configured');
    expect(forwarding.textContent).not.toContain('pending');
    expect(forwarding.getAttribute('data-tone')).toBeNull();
    expect((forwarding.querySelector('[data-testid="status-indicator"]') as HTMLElement).dataset['tone']).toBe('neutral');
  });

  it('verifies the chain on request', () => {
    byTestId<HTMLButtonElement>('access-audit-verify')!.click();
    expect(operators.verify).toHaveBeenCalledTimes(1);
  });

  it('says so when a session has no entries', async () => {
    await show(operatorActivity({ entries: [] }));
    expect(byTestId('access-audit-entries-empty')).not.toBeNull();
  });
});
