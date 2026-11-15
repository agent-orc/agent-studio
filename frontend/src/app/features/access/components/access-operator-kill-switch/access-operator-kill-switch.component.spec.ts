import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import {
  fakeOperatorAgentsService,
  operatorActivity,
  operatorAgents,
  operatorDrill,
  operatorKill,
  operatorKillSwitch,
} from '../../testing/operator-fixtures';
import { AccessOperatorKillSwitchComponent } from './access-operator-kill-switch.component';

describe('AccessOperatorKillSwitchComponent', () => {
  let operators: ReturnType<typeof fakeOperatorAgentsService>;
  let confirm: { confirm: ReturnType<typeof vi.fn> };
  let fixture: ComponentFixture<AccessOperatorKillSwitchComponent>;
  let element: HTMLElement;

  async function render(status = operatorKillSwitch()): Promise<void> {
    operators = fakeOperatorAgentsService(operatorAgents(), operatorActivity(), status);
    confirm = { confirm: vi.fn(() => Promise.resolve(true)) };
    await TestBed.configureTestingModule({
      imports: [AccessOperatorKillSwitchComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: OperatorAgentsService, useValue: operators },
        { provide: ConfirmDialogService, useValue: confirm },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(AccessOperatorKillSwitchComponent);
    fixture.detectChanges();
    element = fixture.nativeElement as HTMLElement;
  }

  const byTestId = <T extends HTMLElement = HTMLElement>(id: string) => element.querySelector(`[data-testid="${id}"]`) as T | null;

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => TestBed.resetTestingModule());

  it('loads the switch state and shows the last use, the last drill, and the schedule quietly', async () => {
    await render();
    expect(operators.loadKillSwitch).toHaveBeenCalled();
    const last = byTestId('access-kill-last')!;
    expect(last.dataset['state']).toBe('revoked');
    expect(last.dataset['tone']).toBeUndefined();
    expect(last.textContent).toContain('All operator access, Studio, 2 agents, 1 live session');
    expect(byTestId('access-kill-last-operations')!.textContent).toContain('Operations Server principals revoked');
    const drill = byTestId('access-kill-drill')!;
    expect(drill.dataset['state']).toBe('passed');
    expect(drill.getAttribute('role')).toBeNull();
    expect(byTestId('access-kill-drill-result')!.textContent).toContain('Drill passed in 413 ms');
    expect(byTestId('access-kill-drill-result')!.dataset['tone']).toBe('success');
    expect(byTestId('access-kill-next-drill')!.textContent).toContain('Next scheduled drill');
    expect(byTestId('access-kill-kill_1')!.textContent).toContain('Unexpected restarts');
  });

  it('names the standing grants and approval requests a kill ended next to its agents and sessions', async () => {
    await render(operatorKillSwitch({
      kills: [
        operatorKill({ standingGrantIds: ['grant_1'], approvalIds: ['apr_1', 'apr_2'] }),
        operatorKill({ id: 'kill_session', kind: 'session', operatorAgentIds: ['opa_ops'], approvalIds: ['apr_3'] }),
      ],
    }));
    expect(byTestId('access-kill-last')!.textContent).toContain('2 agents, 1 live session, 1 standing grant, 2 approval requests');
    expect(byTestId('access-kill-kill_session')!.textContent).toContain('1 session, 1 approval request');
  });

  it('turns acute only for a pending revocation, a failed drill, or a pending host command', async () => {
    await render(operatorKillSwitch({
      kills: [operatorKill({ operationsServer: { state: 'pending', attempts: 2, lastError: 'http-503', cancelledOperations: 0 } })],
      drills: [operatorDrill({ passed: false, failure: 'operations-server-unreachable', operationsServerState: 'pending', operationsServerMs: null })],
      outOfBandRequestPending: true,
      nextDrillAt: null,
    }));
    expect(byTestId('access-kill-last')!.dataset['tone']).toBe('alert');
    expect(byTestId('access-kill-last-operations')!.textContent).toContain('pending (http-503)');
    expect(byTestId('access-kill-drill')!.getAttribute('role')).toBe('alert');
    expect(byTestId('access-kill-drill-result')!.textContent).toContain('Drill failed: operations-server-unreachable');
    expect(byTestId('access-kill-drill-result')!.dataset['tone']).toBe('danger');
    expect(byTestId('access-kill-pending')).not.toBeNull();
    expect(byTestId('access-kill-next-drill')!.textContent).toContain('Scheduled drills are off');
  });

  it('ends all operator access only after confirmation and sends the trimmed reason', async () => {
    await render(operatorKillSwitch({ kills: [], drills: [] }));
    expect(byTestId('access-kill-last')!.textContent).toContain('Never used');
    expect(byTestId('access-kill-drill')!.textContent).toContain('No drill yet');
    const reason = byTestId<HTMLInputElement>('access-kill-reason')!;
    reason.value = '  Loop on restarts  ';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    operators.endAllAccess.mockResolvedValueOnce(operatorKill());
    confirm.confirm.mockResolvedValueOnce(false);
    byTestId<HTMLButtonElement>('access-kill-end-all')!.click();
    await settle();
    expect(operators.endAllAccess).not.toHaveBeenCalled();
    byTestId<HTMLButtonElement>('access-kill-end-all')!.click();
    await settle();
    expect(confirm.confirm).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'danger', detail: 'Loop on restarts' }));
    expect(operators.endAllAccess).toHaveBeenCalledWith('Loop on restarts');
    expect(byTestId<HTMLInputElement>('access-kill-reason')!.value).toBe('');
  });

  it('runs a drill on request and blocks both actions while one runs', async () => {
    await render();
    byTestId<HTMLButtonElement>('access-kill-run-drill')!.click();
    expect(operators.runDrill).toHaveBeenCalledTimes(1);
    operators.killing.set(true);
    fixture.detectChanges();
    expect(byTestId<HTMLButtonElement>('access-kill-run-drill')!.disabled).toBe(true);
    expect(byTestId<HTMLButtonElement>('access-kill-end-all')!.disabled).toBe(true);
    expect(byTestId('access-kill-run-drill')!.textContent).toContain('Running');
  });
});
