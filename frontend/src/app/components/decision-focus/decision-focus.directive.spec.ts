import { ChangeDetectionStrategy, Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DecisionFocusDirective } from './decision-focus.directive';
import { DECISION_FOCUS_HOLD_MS, DecisionFocusRequestService } from '../../services/decision-focus-request.service';

@Component({
  selector: 'app-decision-focus-host',
  standalone: true,
  imports: [DecisionFocusDirective],
  templateUrl: './decision-focus.directive.spec.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class HostComponent {
  readonly shown = signal(true);
  readonly task = signal({ taskKey: 'demo::agt-1', id: 'agt-1', watchPath: 'demo' });
}

describe('DecisionFocusDirective', () => {
  afterEach(() => {
    TestBed.inject(DecisionFocusRequestService).clear();
    document.body.querySelectorAll('app-decision-focus-host').forEach(node => node.remove());
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  async function setup() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    const fixture = TestBed.createComponent(HostComponent);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    return { fixture, root, area: root.querySelector<HTMLElement>('[data-testid="area"]')!,
      requests: TestBed.inject(DecisionFocusRequestService) };
  }

  it('marks the area as a focusable, named group that is not a Tab stop', async () => {
    const { area } = await setup();
    expect(area.getAttribute('tabindex')).toBe('-1');
    expect(area.getAttribute('role')).toBe('group');
    expect(area.getAttribute('aria-label')).toBe('Decision');
    expect(area.hasAttribute('data-decision-focus')).toBe(true);
  });

  it('takes focus when its card is opened to be decided on, so the primary is the next Tab stop', async () => {
    const { fixture, area, requests } = await setup();
    requests.ask('demo::agt-1');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(area);
  });

  it('ignores a request for another card', async () => {
    const { fixture, area, requests } = await setup();
    requests.ask('demo::agt-2');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).not.toBe(area);
  });

  it('takes focus when the area renders after the request, and keeps it across a re-render', async () => {
    const { fixture, root, requests } = await setup();
    fixture.componentInstance.shown.set(false);
    fixture.detectChanges();
    requests.ask('demo::agt-1');
    fixture.componentInstance.shown.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('[data-testid="area"]'));
  });

  it('ends the request at the next key press, so focus is never pulled back', async () => {
    const { fixture, root, requests } = await setup();
    requests.ask('demo::agt-1');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab' }));
    expect(requests.wants()).toBe(false);
    root.querySelector<HTMLElement>('[data-testid="elsewhere"]')!.focus();
    fixture.componentInstance.shown.set(false);
    fixture.detectChanges();
    fixture.componentInstance.shown.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('[data-testid="elsewhere"]'));
  });

  it('lets an unanswered request lapse', async () => {
    const { requests } = await setup();
    vi.useFakeTimers();
    requests.ask('demo::agt-1');
    expect(requests.wants('demo::agt-1')).toBe(true);
    vi.advanceTimersByTime(DECISION_FOCUS_HOLD_MS + 1);
    expect(requests.wants('demo::agt-1')).toBe(false);
  });
});
