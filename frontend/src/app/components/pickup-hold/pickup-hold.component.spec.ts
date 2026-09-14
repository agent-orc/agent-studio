import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import type { PickupHoldStatus, TaskInfo } from '../../models/task.model';
import { PickupHoldComponent } from './pickup-hold.component';

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

/** The reported AGT-2373 shape: an archived release gate, held for a month. */
const ARCHIVED_GATE: PickupHoldStatus = {
  mechanism: 'dependency-gate',
  reason: 'AGT-2372 is archived and was never released, so no run is left that could open this gate.',
  sinceUtc: '2026-08-11T09:00:00Z',
  heldForSeconds: 34 * 86400,
  unsatisfiable: true,
  resolutions: [
    {
      kind: 'release-target',
      label: 'Release AGT-2372',
      detail: 'Releasing states that the validation this gate stands for no longer has to happen.',
      targetKey: 'AGT-2372',
    },
    {
      kind: 'drop-release-gate',
      label: 'Drop the release gate on AGT-2372',
      detail: 'Remove the releaseGate edge through this card\'s references and re-plan the card.',
      targetKey: 'AGT-2372',
    },
  ],
};

/** The reported AGT-2738 shape: a runner refusal nothing ever rendered. */
const REFUSED_DISPATCH: PickupHoldStatus = {
  mechanism: 'dispatch-rejection',
  reason: "Runner agent-runner-01 refused this card (capability-mismatch): "
    + "Required capability 'task-server:connectivity' is advertised as unavailable.",
  sinceUtc: '2026-09-06T19:47:45Z',
  heldForSeconds: 7 * 86400,
  unsatisfiable: false,
  resolutions: [
    {
      kind: 'restore-runner-capability',
      label: 'Restore the runner capability',
      detail: 'Give agent-runner-01 what it reported missing, or route this project elsewhere.',
    },
  ],
};

describe('PickupHoldComponent', () => {
  let fixture: ComponentFixture<PickupHoldComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PickupHoldComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
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

  it('offers both ways out of an archived gate on the detail variant, and takes neither', () => {
    const root = render(ARCHIVED_GATE, 'detail');

    const items = Array.from(root.querySelectorAll('[data-resolution-kind]'));
    expect(items.map(item => item.getAttribute('data-resolution-kind')))
      .toEqual(['release-target', 'drop-release-gate']);
    expect(items[0].textContent).toContain('Release AGT-2372');
    expect(items[1].textContent).toContain('Drop the release gate on AGT-2372');
    // Offered, never taken: the block carries no control that writes.
    expect(root.querySelectorAll('button')).toHaveLength(0);
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
