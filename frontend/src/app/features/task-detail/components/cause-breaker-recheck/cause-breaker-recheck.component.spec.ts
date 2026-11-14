import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { CauseBreakerService, type CauseBreakerCauseView } from '../../../../services/cause-breaker.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { CauseBreakerRecheckComponent } from './cause-breaker-recheck.component';

function view(overrides: Partial<CauseBreakerCauseView> = {}): CauseBreakerCauseView {
  return {
    fingerprint: 'fp-test-report',
    failureClass: 'ReviewInfra/TestReportUnavailable',
    summary: 'review cannot read the test report',
    toolchain: 'tool:dotnet',
    causeKey: 'DEMO-2',
    probeTaskKey: null,
    probeKey: null,
    probeState: null,
    resolving: false,
    held: [
      { taskKey: 'DEMO-4', key: 'DEMO-4', title: 'Fixed test', since: '2026-11-09T08:40:00Z',
        deliveredAt: '2026-11-09T08:30:00Z', recheckFirst: true },
      { taskKey: 'DEMO-1', key: 'DEMO-1', title: 'Broken test', since: '2026-11-09T08:00:00Z',
        deliveredAt: '2026-11-09T07:50:00Z', recheckFirst: false },
    ],
    ...overrides,
  };
}

async function mount(job: Partial<TaskInfo>, answers: (CauseBreakerCauseView | null)[]) {
  const forCause = vi.fn(() => of(answers.length > 1 ? answers.shift()! : answers[0]));
  const recheck = vi.fn(() => of({}));
  const navigation = { openReferenceOrNotify: vi.fn(() => true) };
  await TestBed.configureTestingModule({
    imports: [CauseBreakerRecheckComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: CauseBreakerService, useValue: { forCause, recheck } },
      { provide: TaskReferenceNavigationService, useValue: navigation },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(CauseBreakerRecheckComponent);
  fixture.componentRef.setInput('job', {
    id: 'DEMO-2', key: 'DEMO-2', state: '1-preparation', ...job,
  } as TaskInfo);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, host: fixture.nativeElement as HTMLElement, forCause, recheck, navigation };
}

describe('CauseBreakerRecheckComponent (AGT-3617)', () => {
  it('asks nothing for an accepted card', async () => {
    const { host, forCause } = await mount({ state: '6-completed' }, [view()]);
    expect(forCause).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="cause-breaker-recheck"]')).toBeNull();
  });

  it('stays empty for an orchestrator card that holds nothing', async () => {
    const { host, forCause } = await mount({}, [null]);
    expect(forCause).toHaveBeenCalledWith('DEMO-2');
    expect(host.querySelector('[data-testid="cause-breaker-recheck"]')).toBeNull();
  });

  it('names the hold and offers one primary re-check of the newest delivery', async () => {
    const { host, recheck } = await mount({}, [view(), view({ probeTaskKey: 'DEMO-4', probeKey: 'DEMO-4', probeState: 'running' })]);
    expect(host.querySelector('[data-testid="cause-breaker-recheck-line"]')?.textContent)
      .toContain('2 cards are held behind this card: review cannot read the test report');
    expect(host.textContent).toContain('newest delivery (DEMO-4)');
    const primary = host.querySelectorAll('.cause-recheck__primary');
    expect(primary.length).toBe(1);
    expect(primary[0].textContent?.trim()).toBe('Re-check now');
    // Only the card that is not re-checked first offers its own re-check.
    expect(host.querySelector('[data-testid="cause-breaker-recheck-card-DEMO-4"]')).toBeNull();
    expect(host.querySelector('[data-testid="cause-breaker-recheck-card-DEMO-1"]')).not.toBeNull();

    (primary[0] as HTMLButtonElement).click();
    expect(recheck).toHaveBeenCalledWith('fp-test-report', undefined);
  });

  it('re-checks the card the owner chose', async () => {
    const { fixture, host, recheck } = await mount({}, [view()]);
    (host.querySelector('[data-testid="cause-breaker-recheck-card-DEMO-1"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(recheck).toHaveBeenCalledWith('fp-test-report', 'DEMO-1');
  });

  it('shows a running re-check and does not offer a second one', async () => {
    const { host } = await mount({}, [view({ probeTaskKey: 'DEMO-4', probeKey: 'DEMO-4', probeState: 'running' })]);
    expect(host.querySelector('[data-testid="cause-breaker-recheck-state"]')?.textContent).toContain('Re-checking');
    expect((host.querySelector('[data-testid="cause-breaker-recheck-now"]') as HTMLButtonElement).disabled).toBe(true);
    expect(host.querySelector('[data-testid="cause-breaker-recheck-card-DEMO-1"]')).toBeNull();
  });

  it('states a refused re-check', async () => {
    const { fixture, host, recheck } = await mount({}, [view()]);
    recheck.mockReturnValueOnce(throwError(() => ({ error: { message: 'No waiting card could be released for a re-check.' } })));
    (host.querySelector('[data-testid="cause-breaker-recheck-now"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="cause-breaker-recheck-error"]')?.textContent)
      .toContain('No waiting card could be released');
  });

  it('opens a held card from its key', async () => {
    const { host, navigation } = await mount({}, [view()]);
    (host.querySelector('[data-key="DEMO-1"] .cause-recheck__link') as HTMLButtonElement).click();
    expect(navigation.openReferenceOrNotify).toHaveBeenCalledWith('DEMO-1');
  });
});
