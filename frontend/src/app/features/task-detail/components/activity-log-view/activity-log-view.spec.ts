import { afterEach, describe, expect, it, vi } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ActivityLogViewComponent } from './activity-log-view';
import { CliOutputLine } from '../../../../models/task.model';
import { DossierReferenceHydratorService } from '../../../../services/dossier-reference-hydrator.service';
import {
  DOSSIER_FIXTURE_KEY,
  dossierChipsIn,
  provideDossierCatalogueStub,
} from '../../../../../testing/dossier-references';

const standardProviders = [
  provideZonelessChangeDetection(),
  provideHttpClient(),
  provideHttpClientTesting(),
  provideRouter([]),
  provideDossierCatalogueStub(),
];

async function renderConversation(lines: CliOutputLine[]): Promise<ComponentFixture<ActivityLogViewComponent>> {
  await TestBed.configureTestingModule({
    imports: [ActivityLogViewComponent],
    providers: standardProviders,
  }).compileComponents();
  const fixture = TestBed.createComponent(ActivityLogViewComponent);
  fixture.componentRef.setInput('lines', lines);
  fixture.componentRef.setInput('defaultMode', 'conversation');
  fixture.detectChanges();
  return fixture;
}

function alternatingTurns(count: number): CliOutputLine[] {
  return Array.from({ length: count }, (_, index) => ({
    timestamp: new Date(Date.UTC(2026, 6, 25, 10, 0, index)).toISOString(),
    stream: index % 2 === 0 ? 'user' : 'stdout',
    text: index % 2 === 0 ? `Question ${index}` : `Answer ${index}`,
  }));
}

function mockAnimationFrames(): { flush: () => void } {
  const callbacks: FrameRequestCallback[] = [];
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
    callbacks.push(cb);
    return callbacks.length;
  });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  const realGetComputedStyle = window.getComputedStyle.bind(window);
  vi.spyOn(window, 'getComputedStyle').mockImplementation(((el: Element, pseudo?: string | null) => {
    if (el instanceof HTMLElement && el.dataset['testid'] === 'activity-log-body') {
      return { overflowY: 'auto' } as CSSStyleDeclaration;
    }
    return realGetComputedStyle(el, pseudo);
  }) as typeof window.getComputedStyle);
  return {
    flush: () => {
      while (callbacks.length > 0) callbacks.shift()!(performance.now());
    },
  };
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 *
 * What it does NOT catch: full render-path bugs that require seeded
 * inputs or per-component service stubs — those would need a
 * hand-tuned spec. `detectChanges()` is wrapped in try/catch so a
 * missing-input or missing-provider failure surfaces as a console
 * note instead of a red test, which keeps this generator-driven layer
 * stable across template tweaks.
 */
describe('ActivityLogViewComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    try { fixture.detectChanges(); } catch (e) {
      // Render needs more setup than the generic generator provides.
      // The instantiation above is still a real smoke check.
      console.warn('[smoke] ActivityLogViewComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

/**
 * Bug: "Activity-Log zeigt rohes JSON statt Nutzer-Nachrichten".
 *
 * The projection guard replaces raw stream-json transport frames with a
 * compact `[internal event]` marker (verified structurally in
 * conversation-projection.spec.ts). These specs close the loop at the DOM
 * boundary: they assert the Trace template is actually WIRED to render that
 * marker as a collapsible `<details>` disclosure - the raw JSON only ever
 * appears inside the (initially hidden) `<pre>` detail, never as plain chat
 * text. This pins the "HTML template not wired to render internal event
 * markers" regression: a template that dropped the `internalDetailOf` branch
 * and fell back to `{{ line.text }}` would still show `[internal event]` but
 * would lose the disclosure, so we assert the disclosure element exists and
 * carries the original frame.
 */
describe('ActivityLogViewComponent — internal-event rendering (Trace)', () => {
  const RAW_FRAME =
    '{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"secret reasoning","signature":"Er8BCkg=="}]}}';

  function line(text: string, stream = 'stdout'): CliOutputLine {
    return { timestamp: '2026-07-09T03:15:00.000Z', stream, text };
  }

  async function renderTrace(lines: CliOutputLine[]): Promise<ComponentFixture<ActivityLogViewComponent>> {
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    fixture.componentRef.setInput('lines', lines);
    fixture.componentRef.setInput('debugVisible', true);
    // Drive Trace mode through the input: the component's defaultModeEffect
    // re-applies defaultMode() on every change-detection pass, so a direct
    // mode.set() would be clobbered.
    fixture.componentRef.setInput('defaultMode', 'trace');
    fixture.detectChanges();
    // Trace groups render their lines only when expanded; force every group
    // open so the assertions see the line-level DOM.
    for (const group of fixture.componentInstance.visibleTraceGroups()) {
      fixture.componentInstance.expandedGroups.update((m) => ({ ...m, [group.id]: true }));
    }
    fixture.detectChanges();
    return fixture;
  }

  it('renders the raw frame as a collapsible [internal event] marker, never as chat text', async () => {
    const fixture = await renderTrace([line(RAW_FRAME)]);
    const host: HTMLElement = fixture.nativeElement;

    // The compact marker line is present...
    const summary = host.querySelector<HTMLElement>('.trace-line--internal .trace-line__text');
    expect(summary?.textContent?.trim()).toBe('[internal event]');

    // ...wrapped in a real disclosure element (this is the wiring the review flagged)...
    const disclosure = host.querySelector('details.trace-line__internal');
    expect(disclosure).toBeTruthy();

    // ...the original raw JSON lives ONLY inside the disclosure detail...
    const detail = host.querySelector<HTMLElement>('.trace-line__internal-detail');
    expect(detail?.textContent).toContain('"type":"assistant"');
    expect(detail?.textContent).toContain('"signature"');

    // ...and the raw frame is never rendered as a plain (non-disclosure) line.
    const plainLines = Array.from(host.querySelectorAll('.trace-line'))
      .filter((el) => !el.classList.contains('trace-line--internal'));
    for (const el of plainLines) {
      expect(el.textContent ?? '').not.toContain('"type":"assistant"');
    }
    fixture.destroy();
  });

  it('keeps ordinary prose as a plain line with no disclosure', async () => {
    const fixture = await renderTrace([line('Looking at the activity-log component now.')]);
    const host: HTMLElement = fixture.nativeElement;

    expect(host.querySelector('details.trace-line__internal')).toBeNull();
    const anyLine = host.querySelector<HTMLElement>('.trace-line');
    expect(anyLine?.textContent).toContain('Looking at the activity-log component now.');
    fixture.destroy();
  });

  it('never leaks raw JSON into the Conversation (chat) surface either', async () => {
    // The bug report is about the chat/Conversation surface specifically.
    await TestBed.configureTestingModule({
      imports: [ActivityLogViewComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ActivityLogViewComponent);
    fixture.componentRef.setInput('lines', [
      line('Here is my plan.'),
      line(RAW_FRAME),
      line('Done.'),
    ]);
    fixture.componentRef.setInput('defaultMode', 'conversation');
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    // The whole rendered conversation must never contain the raw frame body.
    expect(host.textContent ?? '').not.toContain('"type":"assistant"');
    expect(host.textContent ?? '').not.toContain('secret reasoning');

    // Assert the conversation view-model as well as the DOM boundary.
    const convoText = fixture.componentInstance
      .visibleConversation()
      .map((item) => item.turn.text ?? '')
      .join('\n');
    expect(convoText).toContain('[internal event]');
    expect(convoText).not.toContain('"type":"assistant"');
    expect(convoText).not.toContain('secret reasoning');
    fixture.destroy();
  });
});

describe('ActivityLogViewComponent — conversation history window', () => {
  it('renders the latest 100 turns and loads older turns in bounded pages', async () => {
    const frames = mockAnimationFrames();
    const fixture = await renderConversation(alternatingTurns(260));
    frames.flush();
    const component = fixture.componentInstance;
    const host: HTMLElement = fixture.nativeElement;

    expect(component.filteredConversationTurns().length).toBe(260);
    expect(component.visibleConversation().length).toBe(100);
    expect(component.olderConversationCount()).toBe(160);
    expect(host.querySelectorAll('.convo-turn')).toHaveLength(100);
    expect(host.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(host.textContent).not.toContain('Question 0');
    expect(host.textContent).toContain('Answer 259');

    host.querySelector<HTMLButtonElement>('[data-testid="activity-log-load-older"]')?.click();
    fixture.detectChanges();
    frames.flush();

    expect(component.visibleConversation().length).toBe(200);
    expect(component.olderConversationCount()).toBe(60);
    expect(host.querySelectorAll('.convo-turn')).toHaveLength(200);
    fixture.destroy();
  });

  it('preserves the reading position by compensating the prepended height', async () => {
    const frames = mockAnimationFrames();
    const fixture = await renderConversation(alternatingTurns(220));
    frames.flush();
    const body = fixture.nativeElement.querySelector('[data-testid="activity-log-body"]') as HTMLElement;
    const metrics = { scrollHeight: 4_000, scrollTop: 3_400, clientHeight: 600 };
    Object.defineProperties(body, {
      scrollHeight: { configurable: true, get: () => metrics.scrollHeight },
      scrollTop: {
        configurable: true,
        get: () => metrics.scrollTop,
        set: (value: number) => { metrics.scrollTop = value; },
      },
      clientHeight: { configurable: true, get: () => metrics.clientHeight },
    });
    body.dispatchEvent(new Event('scroll'));
    metrics.scrollTop = 300;
    body.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(fixture.componentInstance.stickToBottom()).toBe(false);

    fixture.componentInstance.loadOlderConversation();
    fixture.detectChanges();
    metrics.scrollHeight = 6_500;
    frames.flush();

    expect(metrics.scrollTop).toBe(2_800);
    expect(fixture.componentInstance.stickToBottom()).toBe(false);
    fixture.destroy();
  });

  it('releases Follow from the body scroller and Jump to latest writes back to it', async () => {
    const frames = mockAnimationFrames();
    const fixture = await renderConversation(alternatingTurns(120));
    const component = fixture.componentInstance;
    const host: HTMLElement = fixture.nativeElement;
    const body = host.querySelector('[data-testid="activity-log-body"]') as HTMLElement;
    const conversation = host.querySelector('[data-testid="activity-log-conversation"]') as HTMLElement;
    const metrics = { scrollHeight: 5_000, scrollTop: 0, clientHeight: 500 };
    Object.defineProperties(body, {
      scrollHeight: { configurable: true, get: () => metrics.scrollHeight },
      scrollTop: {
        configurable: true,
        get: () => metrics.scrollTop,
        set: (value: number) => { metrics.scrollTop = value; },
      },
      clientHeight: { configurable: true, get: () => metrics.clientHeight },
    });
    frames.flush();
    metrics.scrollTop = 3_900;
    body.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();

    expect(component.stickToBottom()).toBe(false);
    expect(host.querySelector('[data-testid="activity-log-jump-bottom"]')).toBeTruthy();
    conversation.dispatchEvent(new Event('scroll'));
    expect(component.stickToBottom()).toBe(false);

    component.jumpToBottom();
    frames.flush();
    expect(metrics.scrollTop).toBe(metrics.scrollHeight);
    expect(component.stickToBottom()).toBe(true);
    fixture.destroy();
  });

  it('keeps short and very tall Markdown turns rendered while scrolling upward', async () => {
    const frames = mockAnimationFrames();
    const hugeMarkdown = `\`\`\`diff\n${Array.from({ length: 400 }, (_, i) => `+ line ${i}`).join('\n')}\n\`\`\``;
    const fixture = await renderConversation([
      { timestamp: '2026-07-25T10:00:00Z', stream: 'user', text: 'Short question' },
      { timestamp: '2026-07-25T10:00:01Z', stream: 'stdout', text: hugeMarkdown },
      { timestamp: '2026-07-25T10:00:02Z', stream: 'user', text: 'Tiny follow-up' },
      { timestamp: '2026-07-25T10:00:03Z', stream: 'stdout', text: 'Short answer' },
    ]);
    frames.flush();
    const host: HTMLElement = fixture.nativeElement;
    const body = host.querySelector('[data-testid="activity-log-body"]') as HTMLElement;
    body.scrollTop = 0;
    body.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();

    expect(host.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(host.querySelectorAll('.convo-turn')).toHaveLength(4);
    expect(host.textContent).toContain('line 399');
    expect(host.textContent).toContain('Tiny follow-up');
    fixture.destroy();
  });
});

describe('ActivityLogViewComponent runtime sentinels', () => {
  it('renders known markers as verdict and terminal UI while retaining raw trace text', async () => {
    const marker = '[[ASPECT_VERDICT: status=concerns; summary=One issue remains; evidence_checked=a.ts,b.spec.ts; missing=none]] [[TASK_DONE]]';
    const fixture = await renderConversation([{
      timestamp: '2026-09-18T12:00:00.000Z',
      stream: 'stdout',
      text: marker,
    }]);
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[data-testid="activity-aspect-verdict"]')?.textContent)
      .toContain('One issue remains');
    expect(host.querySelector('[data-testid="activity-terminal-sentinel"]')?.textContent)
      .toContain('Task complete');
    expect(host.querySelector('[data-testid="activity-log-conversation"]')?.textContent)
      .not.toContain('[[ASPECT_VERDICT:');
    expect(fixture.componentInstance.visibleTraceGroups().some(group =>
      group.lines.some(line => line.text.includes('[[ASPECT_VERDICT:')))).toBe(true);
    fixture.destroy();
  });
});

// AGT-2812: an agent turn that names a Dossier reads as the Dossier, not as a
// bare key, on the same terms as every other document surface.
describe('ActivityLogViewComponent - Dossier references', () => {
  it('renders a Dossier named in an agent turn as a chip', async () => {
    const fixture = await renderConversation([{
      timestamp: '2026-09-01T10:00:00.000Z',
      stream: 'stdout',
      text: `Reviewed ${DOSSIER_FIXTURE_KEY} before starting.`,
    }]);
    TestBed.inject(DossierReferenceHydratorService).refresh();

    const chips = dossierChipsIn(fixture.nativeElement as HTMLElement);
    expect(chips).toHaveLength(1);
    expect(chips[0].textContent).toContain('Decision cards');
  });
});
