import { provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import type { PickupHoldStatus, TaskInfo } from '../../models/task.model';
import { NowTickService } from '../../services/now-tick.service';
import { TaskService } from '../../services/task.service';
import { RemoteHostsService, type RemoteHost } from '../../features/remote-hosts';
import { startsWithHttpVerb, type RemedyAction } from '../../models/remedy-action.model';
import { RemedyNavigationService } from '../../services/remedy-navigation.service';
import { controlFor } from '../pickup-hold-resolution/pickup-hold-resolution.component';
import { PickupHoldComponent } from './pickup-hold.component';

const api = (target: string, taskKey?: string): RemedyAction => ({ kind: 'api', target, project: 'demo', taskKey });
const route = (target: string, taskKey?: string): RemedyAction => ({ kind: 'route', target, project: 'demo', taskKey });

function task(pickupHold: PickupHoldStatus | null): TaskInfo {
  return {
    id: 'duplicate-cli-paths',
    taskKey: 'demo::AGT-2373',
    key: 'AGT-2373',
    title: 'Remove the duplicate CLI invocation paths',
    state: '2-ready',
    order: 1,
    agent: 'claude',
    createdAt: '2026-08-11T09:00:00Z',
    watchPath: '/workspace',
    projectName: 'demo',
    folderPath: '/workspace/2-ready/duplicate-cli-paths',
    lastActivity: '2026-08-11T09:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pickupHold,
  } as unknown as TaskInfo;
}

/** Board cards carry only sinceUtc; every age below is read at this clock. */
const NOW = Date.parse('2026-09-14T09:00:00Z');
const now = signal(NOW);

/** The reported AGT-2373 shape: an archived release gate, held for a month. */
const ARCHIVED_GATE: PickupHoldStatus = {
  classification: 'unsatisfiable',
  mechanism: 'dependency-gate',
  reason: 'AGT-2372 is archived and was never released, so no run is left that could open this gate.',
  sinceUtc: '2026-08-11T09:00:00Z',
  unsatisfiable: true,
  resolutions: [
    {
      kind: 'drop-dependency',
      label: 'Drop the dependency',
      detail: 'Remove this waits-on edge.',
      targetKey: 'AGT-2372',
      action: api('edit-waits-on'),
    },
    {
      kind: 'repoint-dependency',
      label: 'Point to a successor card',
      detail: 'Replace the edge with its successor.',
      targetKey: 'AGT-2372',
      action: api('edit-waits-on'),
    },
    {
      kind: 'archive-waiting-card',
      label: 'Archive this waiting card',
      detail: 'Close the waiting card.',
      targetKey: 'AGT-2372',
      action: api('archive-task'),
    },
  ],
};

/** The reported AGT-2738 shape: a runner refusal nothing ever rendered. */
const REFUSED_DISPATCH: PickupHoldStatus = {
  mechanism: 'dispatch-rejection',
  reason: "Runner agent-runner-01 refused this card (capability-mismatch): "
    + "Required capability 'task-server:connectivity' is advertised as unavailable.",
  sinceUtc: '2026-09-06T19:47:45Z',
  unsatisfiable: false,
  resolutions: [
    {
      kind: 'restore-runner-capability',
      label: 'Restore the runner capability',
      detail: 'Give agent-runner-01 what it reported missing, or route this project elsewhere.',
      action: route('execution-hosts'),
    },
    {
      kind: 'retry-dispatch',
      label: 'Re-offer the card',
      detail: 'Clears the recorded refusal so the next dispatch tick offers the card again.',
      action: api('clear-dispatch-rejection'),
    },
  ],
};

describe('PickupHoldComponent', () => {
  let fixture: ComponentFixture<PickupHoldComponent>;
  const hosts = signal<RemoteHost[]>([]);
  const taskService = {
    editTaskWaitsOn: vi.fn(),
    moveJob: vi.fn(),
    setTaskReleased: vi.fn(() => of({ released: true })),
    clearDispatchRejection: vi.fn(() => of({ message: 'The refusal is cleared.' })),
    startJob: vi.fn(() => of({})),
    setJobCliType: vi.fn(() => of({})),
    runTaskIntake: vi.fn(() => of({ outcome: 'Pass', reason: '' })),
    setProjectExecutionLocation: vi.fn(() => of({ executionLocation: 'runner-01' })),
  };
  const navigation = {
    canOpen: vi.fn((action: RemedyAction | null | undefined) => Boolean(action && action.kind !== 'api')),
    open: vi.fn(() => true),
  };

  beforeEach(async () => {
    taskService.editTaskWaitsOn.mockReset();
    taskService.editTaskWaitsOn.mockReturnValue(of({
      waitsOn: [], changed: true, cycleResolved: true, message: 'The cycle is resolved.',
    }));
    taskService.moveJob.mockReset();
    taskService.moveJob.mockReturnValue(of({}));
    for (const mock of [taskService.setTaskReleased, taskService.clearDispatchRejection, taskService.startJob,
      taskService.setJobCliType, taskService.runTaskIntake, taskService.setProjectExecutionLocation, navigation.open]) {
      mock.mockClear();
    }
    await TestBed.configureTestingModule({
      imports: [PickupHoldComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: taskService },
        { provide: NowTickService, useValue: { now } },
        { provide: RemoteHostsService, useValue: { hosts } },
        { provide: RemedyNavigationService, useValue: navigation },
      ],
    }).compileComponents();
    now.set(NOW);
    hosts.set([]);
    fixture = TestBed.createComponent(PickupHoldComponent);
  });

  function render(hold: PickupHoldStatus | null, variant: 'card' | 'detail' = 'card'): HTMLElement {
    fixture.componentRef.setInput('task', task(hold));
    fixture.componentRef.setInput('variant', variant);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders nothing for a card that is genuinely pickable', () => {
    expect(render(null).querySelector('[data-testid="pickup-hold"]')).toBeNull();
  });

  it('explains a closed claim gate for a Ready card routed to that runner', () => {
    hosts.set([{ id: 'runner-a', clientId: 'runner-a', name: 'Coding host',
      claimLoadGate: { state: 'closed', loadPerCore: 5.51, threshold: 4,
        sustainedSeconds: 702, since: '2026-09-14T08:48:00Z', activeSlots: 7 },
    } as RemoteHost]);
    fixture.componentRef.setInput('task', {
      ...task(null), executionLocation: { configuredRunnerId: 'runner-a' },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="pickup-hold-reason"]')?.textContent)
      .toContain('not claiming: load 5.5 per core above 4.0 for 12 min');
  });

  it('opens Project Settings at the execution location from a local coding hold', () => {
    const root = render({
      classification: 'unsatisfiable',
      mechanism: 'execution-placement',
      reason: 'Project demo is placed at local execution, but local coding is disabled on this orchestrator.',
      sinceUtc: '2026-08-11T09:00:00Z',
      heldForSeconds: 60,
      unsatisfiable: true,
      resolutions: [{
        kind: 'route-to-remote-runner',
        label: 'Choose an execution runner',
        detail: 'Open Project Settings and choose an available runner under Execution location.',
        action: { kind: 'setting', target: 'execution-runner', project: 'demo' },
      }],
    }, 'detail');
    const way = root.querySelector('[data-resolution-kind="route-to-remote-runner"]')!;
    const setting = way.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-link-route-to-remote-runner"]')!;

    expect(root.querySelector('[data-testid="pickup-hold-mechanism"]')?.textContent)
      .toContain('Execution placement');
    expect(way.textContent).not.toContain('/api/');
    expect(setting.textContent).toContain('Open Project Settings');
    setting.click();
    expect(navigation.open).toHaveBeenCalledWith({ kind: 'setting', target: 'execution-runner', project: 'demo' });
  });

  it('says an archived gate cannot clear by itself, and how long it has been held', () => {
    const root = render(ARCHIVED_GATE);
    const block = root.querySelector('[data-testid="pickup-hold"]') as HTMLElement;

    expect(block.getAttribute('data-tone')).toBe('blocked');
    expect(block.getAttribute('data-mechanism')).toBe('dependency-gate');
    expect(root.querySelector('[data-testid="pickup-hold-headline"]')?.textContent)
      .toContain('cannot clear by itself');
    expect(root.querySelector('[data-testid="pickup-hold-mechanism"]')?.textContent)
      .toContain('Dependency gate');
    expect(root.querySelector('[data-testid="pickup-hold-reason"]')?.textContent)
      .toContain('AGT-2372 is archived and was never released');
    expect(root.querySelector('[data-testid="pickup-hold-age"]')?.textContent)
      .toContain('held for 34d');
  });

  it('ages the hold from sinceUtc on the shared clock, without a read-time counter', () => {
    now.set(Date.parse('2026-08-11T11:05:00Z'));
    const root = render(ARCHIVED_GATE);
    expect(root.querySelector('[data-testid="pickup-hold-age"]')?.textContent)
      .toContain('held for 2h05m');

    now.set(NOW);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="pickup-hold-age"]')?.textContent)
      .toContain('held for 34d');
  });

  it('offers all three operator decisions for an unsatisfiable gate', () => {
    const root = render(ARCHIVED_GATE, 'detail');

    const items = Array.from(root.querySelectorAll('[data-resolution-kind]'));
    expect(items.map(item => item.getAttribute('data-resolution-kind')))
      .toEqual(['drop-dependency', 'repoint-dependency', 'archive-waiting-card']);
    expect(items[0].textContent).toContain('Drop the dependency');
    expect(items[1].textContent).toContain('Point to a successor card');
    expect(items[2].textContent).toContain('Archive this waiting card');
    expect(root.querySelectorAll('button')).toHaveLength(3);
  });

  it('gives every backend resolution kind a control from its typed action', () => {
    // The kind-to-action map of PickupHoldPolicy (pinned by the backend
    // EveryResolutionKind_CarriesItsTypedAction_AndNoHttpDetail test).
    const emitted: [string, RemedyAction][] = [
      ['assign-agent', api('set-cli-type')],
      ['decide-escalation', api('move-to-escalated')],
      ['wait-out-backoff', api('start-run')],
      ['pass-intake', api('run-intake')],
      ['drop-dependency', api('edit-waits-on')],
      ['repoint-dependency', api('edit-waits-on')],
      ['archive-waiting-card', api('archive-task')],
      ['release-target', api('release-task', 'LIB-2')],
      ['drop-release-gate', api('edit-waits-on')],
      ['await-target', route('task', 'LIB-1')],
      ['drop-cycle-edge', api('edit-waits-on', 'APP-1')],
      ['restore-runner-capability', route('execution-hosts')],
      ['retry-dispatch', api('clear-dispatch-rejection')],
      ['route-to-remote-runner', { kind: 'setting', target: 'execution-runner', project: 'demo' }],
    ];
    for (const [kind, action] of emitted) {
      const control = controlFor(action, { kind, label: kind, detail: '', targetKey: 'APP-2', action }, navigation);
      expect(control, kind).not.toBe('none');
    }
  });

  it('renders a resolution without a typed action as explanation, never as a button', () => {
    const root = render({
      ...ARCHIVED_GATE,
      resolutions: [{
        kind: 'release-target',
        label: 'Release AGT-2372',
        detail: 'An older payload without an action.',
        targetKey: 'AGT-2372',
      }],
    }, 'detail');

    expect(root.querySelector('[data-resolution-kind="release-target"]')?.textContent)
      .toContain('Release AGT-2372');
    expect(root.querySelector('button')).toBeNull();
  });

  it('releases the target only after the operator confirms', () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    const root = render({
      ...ARCHIVED_GATE,
      classification: 'satisfiable-soon',
      unsatisfiable: false,
      resolutions: [{
        kind: 'release-target', label: 'Release LIB-2', detail: 'Only an operator may decide that.',
        targetKey: 'LIB-2', action: api('release-task', 'LIB-2'),
      }],
    }, 'detail');
    const release = root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-decision-release-target"]')!;

    release.click();
    expect(taskService.setTaskReleased).not.toHaveBeenCalled();
    release.click();
    expect(taskService.setTaskReleased).toHaveBeenCalledWith('LIB-2', true);
    confirm.mockRestore();
  });

  it('re-offers a refused card and links the capability fix to Execution Hosts', () => {
    const root = render(REFUSED_DISPATCH, 'detail');

    root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-decision-retry-dispatch"]')!.click();
    expect(taskService.clearDispatchRejection).toHaveBeenCalledWith('duplicate-cli-paths', '/workspace');
    fixture.detectChanges();
    expect(root.textContent).toContain('The refusal is cleared.');

    const link = root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-link-restore-runner-capability"]')!;
    expect(link.textContent).toContain('Open Execution Hosts');
    link.click();
    expect(navigation.open).toHaveBeenCalledWith(route('execution-hosts'));
  });

  it('keeps a decision label as a heading when the click does something else', () => {
    const root = render({
      ...REFUSED_DISPATCH,
      mechanism: 'crash-backoff',
      resolutions: [
        { kind: 'wait-out-backoff', label: 'Wait out the cooldown', detail: 'It expires on its own.', action: api('start-run') },
        { kind: 'assign-agent', label: 'Assign a CLI agent', detail: 'Change the agent.', action: api('set-cli-type') },
        { kind: 'await-target', label: 'Finish LIB-1', detail: 'The gate opens.', targetKey: 'LIB-1', action: route('task', 'LIB-1') },
      ],
    }, 'detail');

    const start = root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-decision-wait-out-backoff"]')!;
    expect(start.textContent?.trim()).toBe('Start the run now');
    start.click();
    expect(taskService.startJob).toHaveBeenCalledWith('duplicate-cli-paths', '/workspace');

    root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-decision-assign-agent"]')!.click();
    expect(taskService.setJobCliType).toHaveBeenCalledWith('duplicate-cli-paths', 'claude', '/workspace');

    root.querySelector<HTMLButtonElement>('[data-testid="pickup-hold-link-await-target"]')!.click();
    expect(navigation.open).toHaveBeenCalledWith(route('task', 'LIB-1'));
    for (const text of Array.from(root.querySelectorAll('[data-resolution-kind] button, [data-resolution-kind] span'))) {
      expect(startsWithHttpVerb(text.textContent)).toBe(false);
    }
  });

  it('does not render a button when a handled edge action lacks its target', () => {
    const root = render({
      ...ARCHIVED_GATE,
      resolutions: [{
        kind: 'drop-dependency',
        label: 'Drop the dependency',
        detail: 'The malformed response omitted the target key.',
        action: api('edit-waits-on'),
      }],
    }, 'detail');

    expect(root.querySelector('[data-resolution-kind="drop-dependency"]')?.textContent)
      .toContain('Drop the dependency');
    expect(root.querySelector('button')).toBeNull();
  });

  it('wires every non-cycle actionable decision to its implemented mutation', () => {
    const prompt = vi.spyOn(window, 'prompt').mockReturnValue('AGT-2400');
    const root = render(ARCHIVED_GATE, 'detail');
    const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('button'));

    buttons[0].click();
    expect(taskService.editTaskWaitsOn).toHaveBeenCalledWith('duplicate-cli-paths', {
      remove: ['AGT-2372'],
      reason: 'Operator dropped unsatisfiable dependency AGT-2372.',
    }, '/workspace');

    buttons[1].click();
    expect(taskService.editTaskWaitsOn).toHaveBeenCalledWith('duplicate-cli-paths', {
      remove: ['AGT-2372'], add: ['AGT-2400'],
      reason: 'Operator re-pointed unsatisfiable dependency AGT-2372 to AGT-2400.',
    }, '/workspace');

    buttons[2].click();
    expect(taskService.moveJob).toHaveBeenCalledWith(
      'duplicate-cli-paths', '7-archive', '/workspace', undefined,
      'Operator archived a card with an unsatisfiable dependency.');
    prompt.mockRestore();
  });

  it('targets the selected source and target when dropping a cycle edge', () => {
    const root = render({
      ...ARCHIVED_GATE,
      reason: "This card's dependsOn chain forms a cycle.",
      resolutions: [
        {
          kind: 'drop-cycle-edge',
          label: 'Drop APP-1 → APP-2',
          detail: 'Remove the waits-on edge from APP-1 to APP-2.',
          sourceKey: 'APP-1',
          targetKey: 'APP-2',
          action: api('edit-waits-on', 'APP-1'),
        },
        {
          kind: 'drop-cycle-edge',
          label: 'Drop APP-2 → APP-3',
          detail: 'Remove the waits-on edge from APP-2 to APP-3.',
          sourceKey: 'APP-2',
          targetKey: 'APP-3',
          action: api('edit-waits-on', 'APP-2'),
        },
        {
          kind: 'drop-cycle-edge',
          label: 'Drop APP-3 → APP-1',
          detail: 'Remove the waits-on edge from APP-3 to APP-1.',
          sourceKey: 'APP-3',
          targetKey: 'APP-1',
          action: api('edit-waits-on', 'APP-3'),
        },
      ],
    }, 'detail');

    const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('[data-resolution-kind="drop-cycle-edge"] button'));
    expect(buttons.map(button => button.textContent?.trim())).toEqual([
      'Drop APP-1 → APP-2',
      'Drop APP-2 → APP-3',
      'Drop APP-3 → APP-1',
    ]);

    buttons[1].click();

    expect(taskService.editTaskWaitsOn).toHaveBeenCalledWith('APP-2', {
      remove: ['APP-3'],
      reason: 'Operator dropped cycle edge APP-2 -> APP-3.',
      requireCycleEdge: true,
    });
    fixture.detectChanges();
    expect(root.textContent).toContain('The cycle is resolved.');
  });

  it('keeps the ways out off the board card, where the reason is the payload', () => {
    const root = render(ARCHIVED_GATE, 'card');

    expect(root.querySelector('[data-testid="pickup-hold-ways-out"]')).toBeNull();
    expect(root.querySelector('[data-testid="pickup-hold-reason"]')).not.toBeNull();
  });

  it('names a refused dispatch as an open hold with its mechanism and age', () => {
    const root = render(REFUSED_DISPATCH);
    const block = root.querySelector('[data-testid="pickup-hold"]') as HTMLElement;

    expect(block.getAttribute('data-tone')).toBe('open');
    expect(block.getAttribute('data-mechanism')).toBe('dispatch-rejection');
    expect(root.querySelector('[data-testid="pickup-hold-mechanism"]')?.textContent)
      .toContain('Dispatch refused');
    expect(root.querySelector('[data-testid="pickup-hold-age"]')?.textContent).toContain('held for 7d');
    // The board card renders the durable rejection record itself, in full and
    // with the code and instant, so the block does not repeat the sentence.
    expect(root.querySelector('[data-testid="pickup-hold-reason"]')).toBeNull();
  });

  it('states the refusal reason on the detail variant, which has no other copy of it', () => {
    const reason = render(REFUSED_DISPATCH, 'detail')
      .querySelector('[data-testid="pickup-hold-reason"]')?.textContent ?? '';

    expect(reason).toContain('agent-runner-01');
    expect(reason).toContain('capability-mismatch');
    expect(reason).toContain('task-server:connectivity');
  });

  it('titles an unknown mechanism instead of leaking the kebab-case key', () => {
    const root = render({ ...REFUSED_DISPATCH, mechanism: 'quota-exhausted' });
    expect(root.querySelector('[data-testid="pickup-hold-reason"]')).not.toBeNull();

    expect(root.querySelector('[data-testid="pickup-hold-mechanism"]')?.textContent)
      .toContain('Quota exhausted');
  });
});
