import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { TaskLiveStatusComponent } from './task-live-status.component';

function task(overrides: Partial<TaskInfo> = {}): TaskInfo {
  const now = Date.now();
  return {
    id: 'AGT-2315',
    taskKey: 'demo::AGT-2315',
    title: 'Show live work',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    createdAt: new Date(now - 60_000).toISOString(),
    watchPath: '/workspace',
    projectName: 'demo',
    folderPath: '/workspace/4-auto-review/AGT-2315',
    lastActivity: new Date(now - 20_000).toISOString(),
    sessionName: null,
    model: 'gpt-5.4-mini',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...overrides,
  };
}

describe('TaskLiveStatusComponent', () => {
  let fixture: ComponentFixture<TaskLiveStatusComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskLiveStatusComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskLiveStatusComponent);
  });

  it('renders the active step with duration, host, model, CLI and next steps', () => {
    const now = Date.now();
    fixture.componentRef.setInput('task', task({
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
      liveStatus: {
        attempt: 4,
        activeStep: {
          stepId: 'aspect-tests-and-evidence',
          displayName: 'Tests and evidence',
          kind: 'aspect',
          startedAt: new Date(now - 40_000).toISOString(),
          model: 'gpt-5.4-mini',
          cliType: 'codex',
        },
        nextSteps: [
          { stepId: 'post-code-review-grade', displayName: 'Code-review quality grade' },
          { stepId: 'post-build-test-gate', displayName: 'Build/test gate' },
          { stepId: 'post-integrate-merge', displayName: 'Integrate merge' },
        ],
        latestEventAt: new Date(now - 40_000).toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain('Review aspect · Tests and evidence');
    expect(root.textContent).toMatch(/running (?:39|40)s/);
    expect(root.textContent).toContain('agent-runner-01');
    expect(root.textContent).toContain('gpt-5.4-mini');
    expect(root.textContent).toContain('via Codex');
    expect(root.textContent).toContain('Code-review quality grade → Build/test gate → Integrate merge');
  });

  it('shows an honest queue position when no step is active', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2,
        activeStep: null,
        nextSteps: [{ stepId: 'aspect-requirement-fit', displayName: 'Requirement fit' }],
        queue: { kind: 'review', position: 3 },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Waiting for review slot · position 3');
    expect(fixture.nativeElement.textContent).toContain('Next');
    expect(fixture.nativeElement.textContent).toContain('Requirement fit');
  });

  it('names the wait reason instead of an empty queue when no slot position is known', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [],
        queue: {
          kind: 'review',
          reason: 'waiting for review executor: agent-runner-01-review is registered and active',
        },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain(
      'Waiting for review slot · waiting for review executor: agent-runner-01-review is registered and active');
  });

  it('keeps detail status to compact CURRENT and NEXT rows', () => {
    fixture.componentRef.setInput('variant', 'detail');
    fixture.componentRef.setInput('task', task({
      state: '2-ready',
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'pre-loop-guard', displayName: 'Loop check' }],
        queue: { kind: 'runner', position: 3 },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.querySelectorAll(':scope > div')).toHaveLength(2);
    expect(root.querySelector('[data-testid="task-live-current"]')?.textContent)
      .toContain('Waiting for runner slot · position 3');
    expect(root.querySelector('[data-testid="task-live-detail-inline"]')?.textContent)
      .toContain('Last activity');
    expect(root.querySelector('[data-testid="task-live-next"]')?.textContent)
      .toContain('Loop check');
  });

  it('shows one dependency wait instead of a stale runner position, then flips to the slot after fulfillment', () => {
    const nextSteps = [{ stepId: 'core-agent-run', displayName: 'Agent execution' }];
    const open = task({
      state: '2-ready',
      waitsOn: {
        blocked: true,
        cycleDetected: false,
        items: [{
          key: 'AGT-2534',
          resolved: true,
          fulfilled: false,
          waitingForRelease: false,
          targetState: '5-human-review',
        }],
      },
      liveStatus: {
        attempt: 2,
        activeStep: null,
        nextSteps,
        queue: { kind: 'runner', position: 4 },
        latestEventAt: new Date().toISOString(),
      },
    });
    fixture.componentRef.setInput('task', open);
    fixture.detectChanges();

    const current = fixture.nativeElement.querySelector('[data-testid="task-live-current"]') as HTMLElement;
    expect(current.textContent).toContain('waits for completion: AGT-2534');
    expect(current.textContent).not.toContain('runner slot');
    expect(current.textContent).not.toContain('position 4');
    expect(fixture.nativeElement.textContent).toContain('Next');
    expect(fixture.nativeElement.textContent).toContain('Agent execution');

    fixture.componentRef.setInput('task', {
      ...open,
      waitsOn: {
        blocked: false,
        cycleDetected: false,
        items: [{
          ...open.waitsOn!.items[0],
          fulfilled: true,
          targetState: '6-completed',
        }],
      },
      liveStatus: {
        ...open.liveStatus!,
        queue: { kind: 'runner', position: 3 },
      },
    });
    fixture.detectChanges();

    expect(current.textContent).toContain('Waiting for runner slot · position 3');
    expect(current.textContent).not.toContain('waits for completion');
    expect(fixture.nativeElement.textContent).toContain('Agent execution');
  });

  // AGT-2378: `runActivity` is classified from the local slot registry + local
  // CLI execution, so a remote run (fenced lease + attempt records, no local
  // process) arrives as `no-active-run` between two pipeline steps. The strip
  // must not claim "No active run" while the lease says otherwise.
  it('does not claim "No active run" for a remote run between steps', () => {
    const recent = new Date(Date.now() - 15_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      lastActivity: recent,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      runner: {
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        hostname: 'agent-runner-01',
        backendName: 'stable',
        isRemote: true,
        leaseId: 'lease-1',
        fencingToken: 7,
        acquiredAt: recent,
      },
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        lastActivityAt: recent,
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
      liveStatus: {
        attempt: 3,
        activeStep: null,
        nextSteps: [{ stepId: 'post-code-review-grade', displayName: 'Code-review quality grade' }],
        queue: null,
        latestEventAt: recent,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).toContain('Running remote on agent-runner-01');
    expect(root.dataset['liveTone']).toBe('active');
  });

  it('still calls out "No active run" when nothing owns the task', () => {
    const recent = new Date(Date.now() - 15_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      lastActivity: recent,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      runner: null,
      executionLocation: {
        state: 'recovering',
        executionKind: 'none',
        connectionState: 'recovering',
        leaseState: 'none',
        trustReason: 'no owner',
      },
      liveStatus: {
        attempt: 3,
        activeStep: null,
        nextSteps: [],
        queue: null,
        latestEventAt: recent,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.textContent).toContain('No active run');
    expect(root.dataset['liveTone']).toBe('stalled');
  });

  it('shows a fresh remote lease as the current run when no local execution or pipeline step exists', () => {
    const now = Date.now();
    fixture.componentRef.setInput('task', task({
      state: '3-progress',
      execution: null,
      runner: {
        runnerId: 'agent-runner-01@linux-host',
        runnerName: 'agent-runner-01',
        hostname: 'linux-host',
        backendName: 'remote',
        isRemote: true,
        leaseId: 'lease-remote',
        fencingToken: 4,
        acquiredAt: new Date(now - 125_000).toISOString(),
      },
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        runnerId: 'agent-runner-01@linux-host',
        hostDisplayName: 'agent-runner-01',
        startedAt: new Date(now - 125_000).toISOString(),
        lastHeartbeat: new Date(now - 2_000).toISOString(),
        lastActivityAt: new Date(now - 2_000).toISOString(),
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'Fresh fenced lease heartbeat.',
      },
      runActivity: { kind: 'no-active-run', attempt: 0 },
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent run' }],
        queue: null,
        latestEventAt: new Date(now - 2_000).toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain('Running remote on agent-runner-01');
    expect(root.textContent).toMatch(/Active for 2m0[45]s/);
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).not.toContain('possible hang');
  });

  it('calls out a possible hang after ten minutes without a step or queue', () => {
    const old = new Date(Date.now() - 12 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      lastActivity: old,
      executionLocation: null,
      liveStatus: {
        attempt: 7,
        activeStep: null,
        nextSteps: [{ stepId: 'post-orchestrator-decision', displayName: 'Final review decision' }],
        queue: null,
        latestEventAt: old,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('stalled');
    expect(root.textContent).toMatch(/No activity for (?:11m59s|12m00s) · possible hang/);
  });

  it('never flags a possible hang in preparation, however long it has been idle', () => {
    const stale = new Date(Date.now() - 93 * 60 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      state: '1-preparation',
      lastActivity: stale,
      executionLocation: null,
      runActivity: { kind: 'no-active-run', attempt: 0 },
      liveStatus: {
        attempt: 1,
        activeStep: null,
        nextSteps: [{ stepId: 'loop-check', displayName: 'Loop check' }],
        queue: null,
        latestEventAt: stale,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('idle');
    expect(root.textContent).not.toContain('possible hang');
    expect(root.textContent).not.toContain('No active run');
    expect(root.textContent).toContain('Preparing');
    // last activity still surfaced, live-ticking via NowTickService
    expect(root.textContent).toMatch(/Last activity (?:92h59m|93h00m) ago/);
  });
});
