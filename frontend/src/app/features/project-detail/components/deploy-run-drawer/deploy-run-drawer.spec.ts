import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { DeployRunDrawerComponent, type DeployRunActionState } from './deploy-run-drawer';
import { ModalStackService } from '../../../../services/modal-stack.service';
import type { DeployPanel, DeployRun, DeploymentLogLine } from '../../state/deploy-area.model';
import { deployRunsFixture, deploymentLogFixture } from '../../testing/deploy-fixtures';

describe('DeployRunDrawerComponent', () => {
  let fixture: ComponentFixture<DeployRunDrawerComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const byTestId = (id: string) => host().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const text = (id: string) => byTestId(id)?.textContent?.replace(/\s+/g, ' ').trim();
  const run = (id: string): DeployRun => deployRunsFixture().find(item => item.id === id)!;
  const logOf = (id: string): DeployPanel<DeploymentLogLine[]> => ({ status: 'ready', value: deploymentLogFixture(id) });

  async function render(value: DeployRun, log: DeployPanel<DeploymentLogLine[]> = logOf(value.id),
    action: DeployRunActionState = { busy: null, error: null }): Promise<void> {
    fixture.componentRef.setInput('run', value);
    fixture.componentRef.setInput('log', log);
    fixture.componentRef.setInput('action', action);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeployRunDrawerComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(DeployRunDrawerComponent);
    document.body.appendChild(host());
  });

  afterEach(() => {
    host().remove();
    TestBed.inject(ModalStackService).clearForTest();
  });

  it('titles the run by trigger and environment and shows its outcome and facts', async () => {
    await render(run('run-b-stage'));

    expect(text('deploy-run-drawer')).toContain('Deploy to Staging');
    expect(text('deploy-run-drawer')).toContain('Release 2026.10.19.1');
    expect(text('deploy-run-outcome')).toBe('Failed');
    expect(text('deploy-run-approval')).toBe('Not required');
    expect(host().textContent).toContain('release-gate');
    expect(host().textContent).toContain('1 min 0 s');
  });

  it('renders each step with its state and its log lines underneath', async () => {
    await render(run('run-b-stage'));

    const steps = Array.from(byTestId('deploy-run-steps')!.children).map(item => item.getAttribute('data-testid'));
    expect(steps).toEqual(['deploy-run-step-publish-static', 'deploy-run-step-health']);
    expect(text('deploy-run-step-publish-static')).toContain('Succeeded');
    expect(text('deploy-run-step-publish-static')).toContain('Claimed by deploy host web-vm.');
    expect(text('deploy-run-step-health')).toContain('Failed');
    expect(text('deploy-run-step-health')).toContain('Health check failed (health-http-failed).');
    expect(byTestId('deploy-run-step-health')!.querySelectorAll('.run-drawer__log li')).toHaveLength(1);
  });

  it('shows who approved a Production run', async () => {
    await render(run('run-a-production'));

    expect(text('deploy-run-approval')).toBe('Approved by local.owner');
    expect(byTestId('deploy-run-approve')).toBeNull();
  });

  it('offers approve, reject, and cancel while a run awaits approval, and emits the choice', async () => {
    await render(run('run-c-production'));
    const chosen: string[] = [];
    fixture.componentInstance.act.subscribe(action => chosen.push(action));

    expect(text('deploy-run-approval')).toBe('Waiting for an Owner or Maintainer');
    expect(text('deploy-run-step-approval')).toContain('Awaiting approval');
    expect(text('deploy-run-step-publish-static')).toContain('Pending');
    byTestId('deploy-run-approve')!.click();
    byTestId('deploy-run-reject')!.click();
    byTestId('deploy-run-cancel')!.click();
    expect(chosen).toEqual(['approve', 'reject', 'cancel']);
  });

  it('offers only cancellation for a queued run and nothing for a finished one', async () => {
    await render({ ...run('run-c-production'), state: 'queued' });
    expect(Array.from(host().querySelectorAll('.run-drawer__actions button')).map(item => item.textContent?.trim())).toEqual(['Abort run']);

    await render(run('run-c-stage-redeploy'));
    expect(text('deploy-run-outcome')).toBe('Cancelled');
    expect(host().querySelector('.run-drawer__actions')).toBeNull();
  });

  it('shows the failed step and deadline and requires a reason before skipping', async () => {
    await render({ ...run('run-b-stage'), state: 'waiting-for-intervention', failedStep: 'health',
      interventionExpiresAt: '2026-10-19T13:00:00Z' });
    const chosen: string[] = [];
    fixture.componentInstance.act.subscribe(action => chosen.push(action));
    fixture.componentInstance.skip.subscribe(reason => chosen.push(reason));
    expect(text('deploy-run-outcome')).toBe('Waiting for intervention');
    expect(text('deploy-run-intervention')).toContain('Health check failed');
    expect((byTestId('deploy-run-skip') as HTMLButtonElement).disabled).toBe(true);
    byTestId('deploy-run-retry')!.click();
    const reason = byTestId('deploy-run-skip-reason') as HTMLTextAreaElement;
    reason.value = 'Known transient health check';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    byTestId('deploy-run-skip')!.click();
    expect(chosen).toEqual(['retry', 'Known transient health check']);
    await render({ ...run('run-b-stage'), id: 'another-run', state: 'waiting-for-intervention' });
    expect((byTestId('deploy-run-skip') as HTMLButtonElement).disabled).toBe(true);
  });

  it('disables the actions while one runs and shows a refusal from the server', async () => {
    await render(run('run-c-production'), logOf('run-c-production'), { busy: 'approve', error: null });
    expect(text('deploy-run-approve')).toBe('Approve…');
    expect((byTestId('deploy-run-reject') as HTMLButtonElement).disabled).toBe(true);

    await render(run('run-c-production'), logOf('run-c-production'),
      { busy: null, error: 'Approval requires the Owner or Maintainer role.' });
    expect(byTestId('deploy-run-action-error')?.getAttribute('role')).toBe('alert');
    expect(text('deploy-run-action-error')).toBe('Approval requires the Owner or Maintainer role.');
  });

  it('keeps the steps while the log loads, fails, or is not on the server', async () => {
    await render(run('run-b-stage'), { status: 'loading' });
    expect(byTestId('deploy-run-log-loading')).not.toBeNull();
    expect(byTestId('deploy-run-steps')!.children).toHaveLength(2);

    await render(run('run-b-stage'), { status: 'error', message: "Unknown deployment 'run-b-stage'." });
    expect(text('deploy-run-log-error')).toBe("Unknown deployment 'run-b-stage'.");

    await render(run('run-b-stage'), { status: 'unavailable' });
    expect(byTestId('deploy-run-log-unavailable')).not.toBeNull();
  });

  it('moves focus to the close button on open', async () => {
    await render(run('run-b-stage'));
    await new Promise(resolve => setTimeout(resolve));

    expect(document.activeElement?.getAttribute('data-testid')).toBe('sidesheet-close');
  });

  it('closes on the close button and on Escape', async () => {
    await render(run('run-b-stage'));
    let closed = 0;
    fixture.componentInstance.closeRequest.subscribe(() => closed++);

    (host().querySelector('[data-testid="sidesheet-close"]') as HTMLButtonElement).click();
    const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    host().dispatchEvent(escape);
    expect(closed).toBe(2);
    expect(escape.defaultPrevented).toBe(true);
  });

  it('takes Escape ahead of a surface registered on the modal stack before it', async () => {
    const stack = TestBed.inject(ModalStackService);
    let lowerClosed = 0;
    stack.push('lower-surface', () => { lowerClosed++; });
    fixture.destroy();
    fixture = TestBed.createComponent(DeployRunDrawerComponent);
    document.body.appendChild(host());
    await render(run('run-b-stage'));
    let closed = 0;
    fixture.componentInstance.closeRequest.subscribe(() => closed++);

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect([closed, lowerClosed]).toEqual([1, 0]);
    fixture.destroy();
    expect(stack.topId()).toBe('lower-surface');
  });
});
