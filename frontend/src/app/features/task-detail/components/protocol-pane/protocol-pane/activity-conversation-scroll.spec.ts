import { afterEach, describe, expect, it, vi } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import { ConversationEvent, MessageEvent } from 'coding-agent-chat/core';
import { ConversationScrollFocusDirective } from '../../../../../directives/conversation-scroll-focus.directive';

@Component({
  selector: 'app-activity-conversation-scroll-host',
  standalone: true,
  imports: [ConversationViewComponent, ConversationScrollFocusDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './activity-conversation-scroll.host.html',
})
class ActivityConversationScrollHost {
  readonly events = signal<ConversationEvent[]>([]);
}

function message(index: number, kind: MessageEvent['kind']): MessageEvent {
  return {
    id: `message-${index}`,
    kind,
    timestamp: new Date(Date.UTC(2026, 6, 28, 10, 0, index)).toISOString(),
    actor: kind,
    body: index % 3 === 0
      ? `Short update ${index}`
      : `Update ${index}\n\n${'A variable-height Markdown line.\n'.repeat(index % 7)}`,
    rawRange: {
      source: 'activity-scroll-regression',
      start: index + 1,
      end: index + 1,
    },
  };
}

function conversationEvents(count: number): ConversationEvent[] {
  const events: ConversationEvent[] = [message(0, 'message.user')];
  for (let index = 1; index < count; index += 1) {
    events.push(message(
      index,
      index % 2 === 0 ? 'message.taskAgent' : 'message.orchestrator',
    ));
  }
  return events;
}

function mockAnimationFrames(): { flush: () => void } {
  const callbacks: FrameRequestCallback[] = [];
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
    callbacks.push(callback);
    return callbacks.length;
  });
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
  return {
    flush: () => {
      while (callbacks.length > 0) {
        callbacks.shift()!(performance.now());
      }
    },
  };
}

async function renderConversation(
  events: ConversationEvent[],
): Promise<ComponentFixture<ActivityConversationScrollHost>> {
  await TestBed.configureTestingModule({
    imports: [ActivityConversationScrollHost],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(ActivityConversationScrollHost);
  fixture.componentInstance.events.set(events);
  fixture.detectChanges();
  return fixture;
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('Activity next-gen conversation scroll contract', () => {
  it('keeps a manual reading position on agent updates and resumes Follow only at the bottom', async () => {
    const frames = mockAnimationFrames();
    const realGetComputedStyle = window.getComputedStyle.bind(window);
    vi.spyOn(window, 'getComputedStyle').mockImplementation(((element: Element, pseudo?: string | null) => {
      if (element instanceof HTMLElement && element.dataset['testid'] === 'activity-scroll-root') {
        return { overflowY: 'auto' } as CSSStyleDeclaration;
      }
      return realGetComputedStyle(element, pseudo);
    }) as typeof window.getComputedStyle);

    const initialEvents = conversationEvents(80);
    const fixture = await renderConversation(initialEvents);
    const conversation = fixture.nativeElement.querySelector(
      '[data-testid="conversation-view"]',
    ) as HTMLElement;
    expect(conversation.tabIndex).toBe(0);
    expect(conversation.getAttribute('aria-label')).toBe('Conversation');
    const root = fixture.nativeElement.querySelector(
      '[data-testid="activity-scroll-root"]',
    ) as HTMLElement;
    const metrics = {
      scrollHeight: 6_000,
      scrollTop: 0,
      clientHeight: 500,
    };
    Object.defineProperties(root, {
      scrollHeight: {
        configurable: true,
        get: () => metrics.scrollHeight,
      },
      scrollTop: {
        configurable: true,
        get: () => metrics.scrollTop,
        set: (value: number) => {
          metrics.scrollTop = Math.max(
            0,
            Math.min(value, metrics.scrollHeight - metrics.clientHeight),
          );
        },
      },
      clientHeight: {
        configurable: true,
        get: () => metrics.clientHeight,
      },
    });
    frames.flush();

    expect(metrics.scrollTop).toBe(5_500);
    expect(conversation.querySelectorAll('.conv__row')).toHaveLength(80);
    expect(conversation.querySelector('[data-testid="conversation-spacer-top"]')).toBeNull();

    metrics.scrollTop = 2_400;
    root.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();
    expect(conversation.querySelector('[data-testid="conversation-jump-latest"]')).toBeTruthy();

    metrics.scrollHeight = 6_400;
    fixture.componentInstance.events.set([
      ...initialEvents,
      message(80, 'message.taskAgent'),
    ]);
    fixture.detectChanges();
    await Promise.resolve();
    frames.flush();

    expect(metrics.scrollTop).toBe(2_400);
    expect(conversation.querySelectorAll('.conv__row')).toHaveLength(81);
    expect(conversation.textContent).toContain('Update 80');

    metrics.scrollTop = metrics.scrollHeight - metrics.clientHeight;
    root.dispatchEvent(new Event('scroll'));
    metrics.scrollHeight = 6_800;
    fixture.componentInstance.events.set([
      ...initialEvents,
      message(80, 'message.taskAgent'),
      message(81, 'message.orchestrator'),
    ]);
    fixture.detectChanges();
    await Promise.resolve();
    frames.flush();

    expect(metrics.scrollTop).toBe(6_300);
    expect(conversation.querySelector('[data-testid="conversation-jump-latest"]')).toBeNull();
    fixture.destroy();
  });
});
