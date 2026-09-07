import { describe, expect, it } from 'vitest';
import {
  buildOrchestratorConversationEvents,
  resolveAttachmentUrl,
  sameOrchestratorChatTurns,
  suppressLocalDuplicates,
} from './orchestrator-side-sheet.util';
import { buildDemoEvents } from './orchestrator-side-sheet.demo-events';
import { parseBugHashtags } from './orchestrator-side-sheet.bug-directive';
import type { OrchestratorChatTurn } from '../../../../features/orchestrator';
import type { ChatEvent } from 'coding-agent-chat/core';

/**
 * Direct coverage for the pure helpers extracted out of the side-sheet
 * controller (MC-2 controller split). These used to be module-private
 * functions exercised only through the component; pulling them into a
 * util file made them unit-addressable, so pin them here.
 */
describe('orchestrator-side-sheet.util', () => {
  describe('parseBugHashtags', () => {
    it('collects hashtags at the start of a line and skips markdown headings', () => {
      expect(parseBugHashtags('#frontend #ux\nsome detail')).toEqual(['frontend', 'ux']);
      // A "# " heading (space after hash) is not a tag.
      expect(parseBugHashtags('# Heading\nbody')).toEqual([]);
    });

    it('dedupes repeated tags and ignores mid-line hashes', () => {
      expect(parseBugHashtags('#bug\ntext with #notatag inline\n#bug')).toEqual(['bug']);
    });
  });

  describe('resolveAttachmentUrl', () => {
    it('routes a chat-attachments path through the per-project GET endpoint', () => {
      expect(resolveAttachmentUrl('demo', 'chat-attachments/shot.png')).toBe(
        '/api/runner/demo/orchestrator-chat/attachments/shot.png',
      );
    });

    it('returns the input unchanged when project or path is missing', () => {
      expect(resolveAttachmentUrl(null, 'chat-attachments/x.png')).toBe('chat-attachments/x.png');
      expect(resolveAttachmentUrl('demo', '')).toBe('');
    });
  });

  describe('suppressLocalDuplicates', () => {
    const turn = (id: string, text: string): OrchestratorChatTurn => ({
      id,
      ts: '2026-07-08T00:00:00Z',
      role: 'user',
      text,
    });

    it('hides the server user turn that a live local turn already represents', () => {
      const server = [turn('s1', 'hello')];
      const local = [turn('l1', 'hello')];
      expect(suppressLocalDuplicates(server, local).map((t) => t.id)).toEqual([]);
    });

    it('keeps server turns that no local turn matches', () => {
      const server = [turn('s1', 'hello'), turn('s2', 'world')];
      const local = [turn('l1', 'hello')];
      expect(suppressLocalDuplicates(server, local).map((t) => t.id)).toEqual(['s2']);
    });
  });

  describe('sameOrchestratorChatTurns', () => {
    const turn: OrchestratorChatTurn = {
      id: 'turn-1',
      ts: '2026-07-23T12:00:00Z',
      role: 'orchestrator',
      text: 'AGT-2235 is ready',
      model: 'gpt-5',
      tokenUsage: {
        model: 'gpt-5',
        thinkingLevel: 'high',
        inputTokens: 10,
        outputTokens: 5,
        cacheReadTokens: 2,
        cacheCreationTokens: 1,
      },
      contextReceipt: {
        scope: 'task',
        contextKey: 'task:Agent Studio/AGT-2235',
        taskKey: 'AGT-2235',
        includedBlocks: ['task metadata', 'status.md'],
        capturedAt: '2026-07-23T11:59:58Z',
      },
      attachments: [{
        alt: 'evidence',
        relativePath: 'chat-attachments/evidence.png',
        mimeType: 'image/png',
      }],
    };

    it('treats a freshly deserialized but unchanged poll as equal', () => {
      const copy = JSON.parse(JSON.stringify([turn])) as OrchestratorChatTurn[];
      expect(sameOrchestratorChatTurns([turn], copy)).toBe(true);
    });

    it('propagates real turn and nested metadata changes', () => {
      expect(sameOrchestratorChatTurns([turn], [{ ...turn, text: 'changed' }])).toBe(false);
      expect(sameOrchestratorChatTurns([turn], [{
        ...turn,
        tokenUsage: { ...turn.tokenUsage!, outputTokens: 6 },
      }])).toBe(false);
      expect(sameOrchestratorChatTurns([turn], [{
        ...turn,
        attachments: [{ ...turn.attachments![0], relativePath: 'changed.png' }],
      }])).toBe(false);
      expect(sameOrchestratorChatTurns([turn], [{
        ...turn,
        contextReceipt: { ...turn.contextReceipt!, includedBlocks: ['task metadata'] },
      }])).toBe(false);
      expect(sameOrchestratorChatTurns([turn], [turn, { ...turn, id: 'turn-2' }])).toBe(false);
    });
  });

  describe('buildOrchestratorConversationEvents', () => {
    it('dedupes optimistic turns, resolves attachments, maps actors, and sorts the transcript', () => {
      const server: OrchestratorChatTurn[] = [
        {
          id: 'persisted-user',
          ts: '2026-07-08T00:01:00Z',
          role: 'user',
          text: 'hello',
        },
        {
          id: 'reply',
          ts: '2026-07-08T00:02:00Z',
          role: 'orchestrator',
          text: 'partial answer',
          model: 'claude-opus-4-8',
          tokenUsage: {
            model: 'claude-opus-4-8',
            thinkingLevel: 'high',
            inputTokens: 20,
            outputTokens: 10,
            cacheReadTokens: 0,
            cacheCreationTokens: 0,
          },
          errorMessage: 'connection closed',
          attachments: [{ alt: 'reply shot', relativePath: 'chat-attachments/reply image.png' }],
        },
      ];
      const local = [{
        id: 'local-user',
        ts: '2026-07-08T00:01:00Z',
        role: 'user' as const,
        text: 'hello',
        pending: true,
      }];
      const inline: ChatEvent[] = [{
        id: 'memory',
        kind: 'memory-refreshed',
        timestamp: '2026-07-08T00:00:00Z',
        summary: 'Context rebuilt',
      }];

      const events = buildOrchestratorConversationEvents(
        server,
        local,
        inline,
        'Agent Studio',
        'project:Agent Studio',
      );

      expect(events.map(event => event.id)).toEqual([
        'memory',
        'local-user',
        'reply',
        'reply:attachment:0',
      ]);
      expect(events[0]).toMatchObject({
        kind: 'system.status',
        category: 'memory-refreshed',
        label: 'Memory refreshed',
      });
      expect(events[1]).toMatchObject({
        kind: 'message.user',
        actor: 'You',
        body: 'hello',
      });
      expect(events[2]).toMatchObject({
        kind: 'message.orchestrator',
        actor: 'Orchestrator',
        severity: 'error',
        model: 'claude-opus-4-8',
        thinkingLevel: 'high',
      });
      expect((events[2] as { body: string }).body).toContain('**Error:** connection closed');
      expect(events[3]).toMatchObject({
        kind: 'artifact.image',
        caption: 'reply shot',
        url: '/api/runner/Agent%20Studio/orchestrator-chat/attachments/reply%20image.png',
      });
      expect(events.every(event => event.rawRange.source === 'project:Agent Studio')).toBe(true);
    });

    it('maps each legacy inline card to the closest semantic conversation event', () => {
      const inline: ChatEvent[] = [
        {
          id: 'tool', kind: 'tool-call', timestamp: '2026-07-08T00:00:00Z',
          summary: 'Read src/app.ts',
        },
        {
          id: 'watchdog', kind: 'watchdog', timestamp: '2026-07-08T00:01:00Z',
          summary: 'Tool phase silent for 90s', severity: 'warn', detail: 'Waiting for output',
        },
        {
          id: 'rate', kind: 'rate-limit', timestamp: '2026-07-08T00:02:00Z',
          summary: '5h window at 78%',
        },
        {
          id: 'decision', kind: 'decision', timestamp: '2026-07-08T00:03:00Z',
          summary: 'Reissue once', detail: 'Open items remain', decisionType: 'reissue',
        },
        {
          id: 'update', kind: 'update', timestamp: '2026-07-08T00:04:00Z',
          summary: 'Index updated',
        },
        {
          id: 'task', kind: 'task', timestamp: '2026-07-08T00:05:00Z',
          summary: 'Created AGT-1', actionLabel: 'Open task',
        },
        {
          id: 'recovered', kind: 'session-recovered', timestamp: '2026-07-08T00:06:00Z',
          summary: 'Retry succeeded',
        },
      ];

      const events = buildOrchestratorConversationEvents([], [], inline, 'demo', 'project:demo');

      expect(events.map(event => event.kind)).toEqual([
        'toolBurst',
        'supervisor.wait',
        'system.status',
        'decision.orchestrator',
        'system.status',
        'system.status',
        'supervisor.wait',
      ]);
      expect(events[0]).toMatchObject({
        count: 1,
        families: { other: 1 },
        samples: { other: 'Read src/app.ts' },
      });
      expect(events[1]).toMatchObject({ state: 'quiet', quietSeconds: 90 });
      expect(events[3]).toMatchObject({
        decisionType: 'reissue',
        reason: 'Reissue once',
        evidence: 'Open items remain',
      });
      expect(events[5]).toMatchObject({ category: 'task', nextStep: 'Open task' });
      expect(events[6]).toMatchObject({ state: 'resumed', quietSeconds: 0 });
    });
  });

  describe('buildDemoEvents', () => {
    it('seeds one card per demo event kind, anchored on baseTs', () => {
      const base = Date.parse('2026-07-08T00:00:00Z');
      const events = buildDemoEvents(base);
      // Six event kinds plus the four decision sub-types = nine cards.
      expect(events).toHaveLength(9);
      expect(events.map((e) => e.kind)).toEqual([
        'tool-call',
        'watchdog',
        'rate-limit',
        'session-recovered',
        'memory-refreshed',
        'decision',
        'decision',
        'decision',
        'decision',
      ]);
      // First card sits exactly on baseTs; later cards are offset forward
      // so the demo timeline renders in a stable chronological order.
      expect(events[0].timestamp).toBe(new Date(base).toISOString());
      const ordered = events.map((e) => Date.parse(e.timestamp));
      expect(ordered).toEqual([...ordered].sort((a, b) => a - b));
    });
  });
});
