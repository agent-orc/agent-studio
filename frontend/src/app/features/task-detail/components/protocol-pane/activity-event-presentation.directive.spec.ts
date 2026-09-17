import { afterEach, describe, expect, it } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import type { ConversationEvent, SystemStatusEvent } from 'coding-agent-chat/core';
import { ActivityEventPresentationDirective } from './activity-event-presentation.directive';
import { presentActivityEvents } from './activity-event-presentation';

@Component({
  selector: 'app-activity-event-presentation-directive-host',
  standalone: true,
  imports: [ConversationViewComponent, ActivityEventPresentationDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './activity-event-presentation-directive-host.spec.html',
})
class ActivityEventPresentationDirectiveHost {
  readonly events = signal<ConversationEvent[]>([]);
}

function runnerStatus(id: string, label: string, start: number): SystemStatusEvent {
  return {
    id,
    kind: 'system.status',
    timestamp: `2026-07-11T10:00:0${start}Z`,
    rawRange: { source: 'AGT-2814', start, end: start },
    category: 'runner',
    severity: 'info',
    label,
    explanation: `${label} detail`,
  };
}

async function renderHost(
  events: ConversationEvent[],
): Promise<ComponentFixture<ActivityEventPresentationDirectiveHost>> {
  await TestBed.configureTestingModule({
    imports: [ActivityEventPresentationDirectiveHost],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ActivityEventPresentationDirectiveHost);
  fixture.componentInstance.events.set(events);
  fixture.detectChanges();
  return fixture;
}

afterEach(() => {
  TestBed.resetTestingModule();
});

/**
 * DOM-level sibling of the "runner status grouping" projection specs in
 * activity-event-presentation.spec.ts. Those specs pin the data shape
 * (`runnerGroupRows`); these pin that the compatibility directive actually
 * wires that shape into the DOM the library renders - a run of consecutive
 * "RUNNER"-labelled rows collapses into one heading with a single trace
 * affordance and the per-fact breakdown nested behind one disclosure,
 * instead of the library repeating one `<li>` (and one "trace" button) per
 * fact.
 */
describe('ActivityEventPresentationDirective — runner-group rendering', () => {
  it('renders a run of consecutive runner rows as exactly one grouped row with a count label', async () => {
    const raw = [
      runnerStatus('r1', 'Runner ready', 1),
      runnerStatus('r2', 'Runner started', 2),
      runnerStatus('r3', 'Runner config', 3),
      runnerStatus('r4', 'Runner finished', 4),
    ];
    const events = presentActivityEvents(raw, 'AGT-2814', null);
    const fixture = await renderHost(events);
    const host: HTMLElement = fixture.nativeElement;

    const rows = host.querySelectorAll<HTMLElement>(
      '[data-testid="conversation-system-status"][data-category="runner-group"]',
    );
    expect(rows).toHaveLength(1);

    const row = rows[0];
    expect(row.querySelector('.status-row__chip')?.textContent).toBe('Runner');
    expect(row.querySelector('.status-row__text')?.textContent).toBe('4 updates');
    fixture.destroy();
  });

  it('nests the per-row breakdown behind a single disclosure', async () => {
    const raw = [
      runnerStatus('r1', 'Runner ready', 1),
      runnerStatus('r2', 'Runner started', 2),
      runnerStatus('r3', 'Runner config', 3),
    ];
    const events = presentActivityEvents(raw, 'AGT-2814', null);
    const fixture = await renderHost(events);
    const host: HTMLElement = fixture.nativeElement;

    const row = host.querySelector<HTMLElement>(
      '[data-testid="conversation-system-status"][data-category="runner-group"]',
    );
    expect(row).toBeTruthy();

    // The breakdown is not visible directly on the row - it lives inside a
    // <details> disclosure that starts collapsed.
    const detail = row!.querySelector<HTMLDetailsElement>('[data-testid="runner-group-detail"]');
    expect(detail).toBeTruthy();
    expect(detail!.open).toBe(false);

    const list = detail!.querySelector<HTMLElement>('[data-testid="runner-group-rows"]');
    expect(list).toBeTruthy();
    const labels = Array.from(list!.querySelectorAll('dt')).map((el) => el.textContent);
    expect(labels).toEqual(['Runner ready', 'Runner started', 'Runner config']);
    const explanations = Array.from(list!.querySelectorAll('dd')).map((el) => el.textContent);
    expect(explanations).toEqual([
      'Runner ready detail',
      'Runner started detail',
      'Runner config detail',
    ]);
    fixture.destroy();
  });

  it('renders exactly one trace affordance for the whole group, not one per collapsed row', async () => {
    const raw = [
      runnerStatus('r1', 'Runner ready', 1),
      runnerStatus('r2', 'Runner started', 2),
      runnerStatus('r3', 'Runner config', 3),
      runnerStatus('r4', 'Runner finished', 4),
    ];
    const events = presentActivityEvents(raw, 'AGT-2814', null);
    const fixture = await renderHost(events);
    const host: HTMLElement = fixture.nativeElement;

    const row = host.querySelector<HTMLElement>(
      '[data-testid="conversation-system-status"][data-category="runner-group"]',
    );
    expect(row).toBeTruthy();
    const traceButtons = row!.querySelectorAll('[data-testid="conversation-status-open-trace"]');
    expect(traceButtons).toHaveLength(1);

    // No leftover ungrouped runner rows sitting next to the group.
    const allStatusRows = host.querySelectorAll('[data-testid="conversation-system-status"]');
    expect(allStatusRows).toHaveLength(1);
    fixture.destroy();
  });

  it('leaves a lone runner fact rendered as an ordinary ungrouped row', async () => {
    const events = presentActivityEvents([runnerStatus('r1', 'Runner ready', 1)], 'AGT-2814', null);
    const fixture = await renderHost(events);
    const host: HTMLElement = fixture.nativeElement;

    const groups = host.querySelectorAll('[data-category="runner-group"]');
    expect(groups).toHaveLength(0);
    const rows = host.querySelectorAll('[data-testid="conversation-system-status"][data-category="runner"]');
    expect(rows).toHaveLength(1);
    expect(rows[0].querySelectorAll('[data-testid="conversation-status-open-trace"]')).toHaveLength(1);
    fixture.destroy();
  });
});
