import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { DeployRunDrawerComponent } from './deploy-run-drawer';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { buildDeployRuns, type DeployRun } from '../../state/deploy-area.model';
import { deployEnvironmentsFixture, deployReleasesFixture } from '../../testing/deploy-fixtures';

describe('DeployRunDrawerComponent', () => {
  let fixture: ComponentFixture<DeployRunDrawerComponent>;
  const host = () => fixture.nativeElement as HTMLElement;
  const run = (id: string): DeployRun => buildDeployRuns(deployEnvironmentsFixture(), deployReleasesFixture()).find(item => item.id === id)!;

  async function render(value: DeployRun): Promise<void> {
    fixture.componentRef.setInput('run', value);
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

  it('titles the run and shows its outcome, evidence, and facts', async () => {
    await render(run('job-c-stage'));
    const text = host().textContent!.replace(/\s+/g, ' ');

    expect(host().querySelector('.sidesheet__title')?.textContent?.trim()).toBe('Deploy to Staging');
    expect(host().querySelector('[data-testid="deploy-run-outcome"]')?.textContent?.trim()).toBe('Failed');
    expect(text).toContain('Health check failed');
    expect(text).toContain('Requested by release-gate');
    expect(text).toContain('web-vm');
    expect(text).toContain('2 min 0 s');
  });

  it('renders the step log in order with each state', async () => {
    await render(run('job-a-stage-rollback'));
    const steps = Array.from(host().querySelectorAll('[data-testid="deploy-run-steps"] li'))
      .map(step => Array.from(step.querySelectorAll('span, strong, time')).map(part => part.textContent!.trim()).join(' | '));

    expect(host().querySelector('.sidesheet__title')?.textContent?.trim()).toBe('Rollback to Staging');
    expect(steps).toHaveLength(3);
    expect(steps[0]).toMatch(/^Succeeded \| Queued \| Requested by automatic-rollback \| Oct \d+, \d{2}:\d{2}:00$/);
    expect(steps[1]).toContain('Claimed by deploy host');
    expect(steps[2]).toContain('Restore previous release and check health');
    expect(steps[2]).toContain('Health check passed');
  });

  it('reads "Not finished" for a run still in flight', async () => {
    const inFlight = { ...run('job-b-stage'), outcome: 'running' as const, finishedAt: null, evidence: null };
    await render(inFlight);

    expect(host().textContent).toContain('Not finished');
    expect(host().querySelector('[data-testid="deploy-run-outcome"]')?.textContent?.trim()).toBe('Running');
  });

  it('moves focus to the close button on open', async () => {
    await render(run('job-b-stage'));
    await new Promise(resolve => setTimeout(resolve));

    expect(document.activeElement?.getAttribute('data-testid')).toBe('sidesheet-close');
  });

  it('closes on the close button and on Escape', async () => {
    await render(run('job-b-stage'));
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
    await render(run('job-b-stage'));
    let closed = 0;
    fixture.componentInstance.closeRequest.subscribe(() => closed++);

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect([closed, lowerClosed]).toEqual([1, 0]);
    fixture.destroy();
    expect(stack.topId()).toBe('lower-surface');
  });
});
