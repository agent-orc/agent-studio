import { describe, expect, it } from 'vitest';
import {
  codexTextModeStderrTranscriptFragment,
  projectConversation,
  type ConversationEvent,
  type SupervisorWaitEvent,
  type SystemStatusEvent,
  type ToolBurstEvent,
} from 'coding-agent-chat/core';
import { sanitizeProjectionLines } from '../conversation-projection';
import {
  formatCompactTokens,
  isPresentedRunnerGroupEvent,
  presentActivityEvents,
  stripLegacyCompletionLines,
  type PresentedRunnerGroupEvent,
  type PresentedToolBurstEvent,
} from './activity-event-presentation';
import {
  artifactBlocks,
  presentArtifactEvents,
} from './artifact-gallery/artifact-gallery.model';

const range = { source: 'AGT-2088', start: 10, end: 12 };
const burst: ToolBurstEvent = {
  id: 'tools', kind: 'toolBurst', timestamp: '2026-07-11T10:00:00Z', runId: 2, rawRange: range,
  count: 1, failures: 0, durationMs: 20, families: { command: 1 },
};

describe('presentActivityEvents', () => {
  it('folds a parser warning into the preceding tool details', () => {
    const events: ConversationEvent[] = [burst, {
      id: 'warning', kind: 'system.parserWarning', timestamp: '2026-07-11T10:00:01Z', runId: 2,
      rawRange: { ...range, end: 13 }, expectedKind: 'tool-result',
      message: 'Tool router reported exit code 1.', dedupeKey: 'router-1', collapsedByDefault: true,
    }];
    const result = presentActivityEvents(events, 'AGT-2088', null);
    expect(result).toHaveLength(1);
    expect(result[0].kind).toBe('toolBurst');
    expect((result[0] as ToolBurstEvent).commands?.[0].output).toContain('Expected event: tool-result');
  });

  it('promotes supported image and document artifacts while keeping unknown files listed', () => {
    const base = presentActivityEvents([{
      ...burst,
      artifacts: ['results/playwright/dark.png', 'results/report.json', 'results/archive.zip'],
    }], 'AGT-2088', 'watch');
    const result = presentArtifactEvents(base, 'AGT-2088', 'watch');
    expect((result[0] as ToolBurstEvent).artifacts).toEqual(['results/archive.zip']);
    expect(result[1]).toMatchObject({
      kind: 'artifact.image', caption: 'results/playwright / dark.png',
      url: '/api/tasks/AGT-2088/thumbnail?path=playwright%2Fdark.png&watchPath=watch&width=360',
      artifactPresentation: {
        kind: 'image',
        url: '/api/tasks/AGT-2088/screenshot?path=playwright%2Fdark.png&watchPath=watch',
        thumbnailUrl: '/api/tasks/AGT-2088/thumbnail?path=playwright%2Fdark.png&watchPath=watch&width=360',
      },
    });
    expect(result[2]).toMatchObject({
      kind: 'artifact.image',
      url: null,
      artifactPresentation: {
        kind: 'json',
        path: 'results/report.json',
        contentUrl: '/api/tasks/AGT-2088/files/results/report.json?watchPath=watch&scope=workspace',
      },
    });
  });

  it('turns contiguous artifact lines in one agent message into one mixed block', () => {
    const result = presentArtifactEvents([{
      id: 'answer',
      kind: 'message.taskAgent',
      actor: 'Codex',
      timestamp: '2026-08-09T10:00:00Z',
      rawRange: range,
      body: [
        'The review bundle is ready:',
        '- [Light view](results/gallery-light.png)',
        '- [Dark view](results/gallery-dark.webp)',
        '- [Delivery changes](results/delivery.diff)',
        '- [Notes](results/gallery-notes.md)',
        '- [Metrics](results/metrics.json)',
        '- [Report](results/report.html)',
      ].join('\n'),
    }], 'AGT-2558', '/workspace');

    expect(result[0]).toMatchObject({ kind: 'message.taskAgent', body: 'The review bundle is ready:' });
    expect(result.slice(1).map((event) => event.kind)).toEqual([
      'artifact.image', 'artifact.image', 'artifact.image', 'artifact.image', 'artifact.image',
      'artifact.image',
    ]);
    const presentations = result.slice(1).map((event) =>
      (event as unknown as { artifactPresentation: { kind: string } }).artifactPresentation.kind);
    expect(presentations).toEqual(['image', 'image', 'diff', 'markdown', 'json', 'html']);
    expect((result[6] as unknown as { artifactPresentation: { url: string } }).artifactPresentation.url)
      .toBe('/api/tasks/AGT-2558/results/report.html?watchPath=%2Fworkspace');
    expect(new Set(result.slice(1).map((event) =>
      (event as unknown as { artifactGroupId: string }).artifactGroupId)).size).toBe(1);
  });

  it('keeps message-linked screenshots in the mixed block without catalogue duplicates', () => {
    const body = [
      'The evidence is ready:',
      '- [Light](results/light.png)',
      '- [Dark](results/dark.png)',
      '- [Delivery](results/delivery.diff)',
      '- [Notes](results/notes.md)',
    ].join('\n');
    const projected = projectConversation({
      source: 'DEMO-5',
      lines: [{
        timestamp: '2026-08-09T10:00:00Z',
        stream: 'stdout',
        text: JSON.stringify({
          type: 'item.completed',
          item: { id: 'gallery', type: 'agent_message', text: body },
        }),
      }],
      screenshots: [{
        caption: 'light.png', sourcePath: 'results/light.png', durablePath: 'results/light.png',
        sourceTool: 'screenshot', timestamp: '2026-08-09T10:00:01Z',
      }, {
        caption: 'dark.png', sourcePath: 'results/dark.png', durablePath: 'results/dark.png',
        sourceTool: 'screenshot', timestamp: '2026-08-09T10:00:02Z',
      }],
    });

    const result = presentArtifactEvents(projected, 'DEMO-5', '/demo');
    const artifacts = result.filter((event) => event.kind === 'artifact.image');
    expect(artifacts).toHaveLength(4);
    expect(artifacts.map((event) =>
      (event as unknown as { artifactPresentation: { kind: string } }).artifactPresentation.kind))
      .toEqual(['image', 'image', 'diff', 'markdown']);
    expect(new Set(artifacts.map((event) =>
      (event as unknown as { artifactGroupId: string }).artifactGroupId)).size).toBe(1);
  });

  it('keeps a single image and unknown artifact line in normal message markdown', () => {
    const body = '- ![Only image](results/only.png)\n- [Archive](results/bundle.zip)';
    const result = presentArtifactEvents([{
      id: 'answer', kind: 'message.taskAgent', actor: 'Codex', timestamp: '2026-08-09T10:00:00Z',
      rawRange: range, body,
    }], 'AGT-2558', null);

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({ kind: 'message.taskAgent', body });
  });

  it('keeps a single tool image in the existing inline row', () => {
    const base = presentActivityEvents([{
      ...burst,
      artifacts: ['results/only.png'],
    }], 'AGT-2558', '/workspace');
    const result = presentArtifactEvents(base, 'AGT-2558', '/workspace');

    expect(result[1]).toMatchObject({
      kind: 'artifact.image',
      url: '/api/tasks/AGT-2558/results/only.png?watchPath=%2Fworkspace',
    });
    expect(artifactBlocks(result)).toEqual([]);
  });

  it('keeps separate contiguous-line groups as separate gallery blocks', () => {
    const result = presentArtifactEvents([{
      id: 'answer', kind: 'message.taskAgent', actor: 'Codex', timestamp: '2026-08-09T10:00:00Z',
      rawRange: range,
      body: [
        '- [Light](results/first-light.png)',
        '- [Dark](results/first-dark.png)',
        '',
        '- [Light](results/second-light.png)',
        '- [Dark](results/second-dark.png)',
      ].join('\n'),
    }], 'AGT-2558', '/workspace');

    expect(artifactBlocks(result).map((block) => block.artifacts.map((artifact) => artifact.path)))
      .toEqual([
        ['results/first-light.png', 'results/first-dark.png'],
        ['results/second-light.png', 'results/second-dark.png'],
      ]);
  });

  it('projects edit files relative to the run worktree and retains absolute tooltip paths', () => {
    const root = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\frontend';
    const first = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\frontend\\src\\app\\campaign.ts';
    const second = 'C:\\Users\\operator\\AppData\\Local\\Temp\\ass-worktrees\\Agent-Studio-Marketing\\MKT-20\\docs\\brief.md';
    const result = presentActivityEvents([{
      ...burst,
      count: 5,
      families: { edit: 5 },
      files: [first, first, second],
      samples: { edit: `Edit ${first}` },
    }], 'MKT-20', null, {
      worktreeRootsByRun: new Map([[2, root]]),
    });

    const presented = result[0] as PresentedToolBurstEvent;
    expect(presented.files).toEqual([
      'frontend/src/app/campaign.ts',
      'docs/brief.md',
    ]);
    expect(presented.fileDetails).toEqual([
      { displayPath: 'frontend/src/app/campaign.ts', fullPath: first.replace(/\\/g, '/') },
      { displayPath: 'docs/brief.md', fullPath: second.replace(/\\/g, '/') },
    ]);
    expect(presented.samples?.['edit']).toBe('Edit frontend/src/app/campaign.ts');
    expect(presented.rowPresentation).toEqual({
      kind: 'edit',
      primaryLabel: '5 Edits · 2 files',
      mixLabel: '',
      outcomeLabel: 'all ok',
      pathLabel: 'frontend/src/app/campaign.ts +1 more',
      fileTooltip: `${first.replace(/\\/g, '/')}\n${second.replace(/\\/g, '/')}`,
    });
  });

  it('builds a top-two tool mix, success aggregate, and duration-ready row data', () => {
    const result = presentActivityEvents([{
      ...burst,
      count: 8,
      families: { search: 1, read: 2, command: 5 },
      failures: 0,
      durationMs: 88_000,
    }], 'AGT-2088', null);

    const presented = result[0] as PresentedToolBurstEvent;
    expect(presented.rowPresentation).toEqual({
      kind: 'tool',
      primaryLabel: '8 Tool calls',
      mixLabel: 'shell ×5, read ×2',
      outcomeLabel: 'all ok',
      pathLabel: undefined,
      fileTooltip: undefined,
    });
    expect(presented.durationMs).toBe(88_000);
  });

  it('keeps adjacent failed clusters separate and marks every aggregate as failed', () => {
    const failed = { ...burst, failures: 1, count: 3, families: { command: 2, read: 1 } };
    const result = presentActivityEvents([
      { ...failed, id: 'failed-1' },
      { ...failed, id: 'failed-2' },
    ], 'AGT-2088', null) as PresentedToolBurstEvent[];

    expect(result).toHaveLength(2);
    expect(result.every((event) => event.kind === 'toolBurst')).toBe(true);
    expect(result.every((event) => event.rowPresentation.outcomeLabel === '1 failed')).toBe(true);
  });

  it('recovers a relative edit file from the shared projector sample', () => {
    const root = '/home/runner/worktrees/AGT-2088';
    const result = presentActivityEvents([{
      ...burst,
      count: 2,
      families: { edit: 2 },
      files: undefined,
      samples: { edit: 'Edit ./frontend/src/app/protocol.ts, Edit ./frontend/src/app/protocol.ts' },
    }], 'AGT-2088', null, {
      fallbackWorktreeRoot: root,
    }) as PresentedToolBurstEvent[];

    expect(result[0]).toMatchObject({
      files: ['frontend/src/app/protocol.ts'],
      fileDetails: [{
        displayPath: 'frontend/src/app/protocol.ts',
        fullPath: `${root}/frontend/src/app/protocol.ts`,
      }],
      rowPresentation: {
        primaryLabel: '2 Edits · 1 file',
        pathLabel: 'frontend/src/app/protocol.ts',
      },
    });
  });

  it('leaves supervisor events unchanged for the library-owned CAC-21 grouping', () => {
    const waits: SupervisorWaitEvent[] = [{
      id: 'quiet', kind: 'supervisor.wait', timestamp: '2026-07-11T10:00:00Z', runId: 2,
      rawRange: range, severity: 'warn', state: 'quiet', quietSeconds: 35,
      reason: 'phase=TurnInProgress silence=35s allowed=180/600s',
    }, {
      id: 'resumed', kind: 'supervisor.wait', timestamp: '2026-07-11T10:00:05Z', runId: 2,
      rawRange: range, severity: 'info', state: 'resumed', quietSeconds: 0,
    }];

    expect(presentActivityEvents(waits, 'AGT-2088', null)).toEqual(waits);
  });

  it('upgrades legacy completion prose into a typed label and turn metric', () => {
    const result = presentActivityEvents([{
      id: 'done', kind: 'system.status', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      category: 'result', label: 'Result', explanation: 'Turn completed (tokens: 14587328)',
    }], 'AGT-2088', null);
    expect(result[0]).toMatchObject({ label: 'Turn completed', explanation: '' });
    expect(result[1]).toMatchObject({ kind: 'metric.token', scope: 'turn', inputTokens: 14_587_328 });
    expect(JSON.stringify(result)).not.toContain('Turn completed (tokens:');
    expect(formatCompactTokens(14_587_328)).toBe('14,6M');
  });

  it('removes the synthetic current-task title marker from the activity feed', () => {
    const result = presentActivityEvents([{
      id: 'task', kind: 'taskMarker', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      marker: '4-auto-review', lane: '4-auto-review', jobId: 'AGT-2168',
      title: 'Completion judge: semantically interpret final-attempt prose with typed evidence',
    }, {
      id: 'run', kind: 'runMarker', timestamp: '2026-07-11T10:00:01Z', rawRange: range,
      marker: 'complete', runId: 1,
    }], 'AGT-2088', null);

    expect(result.map((event) => event.kind)).toEqual(['runMarker']);
  });

  it('keeps reissue as decision information instead of a link-like next action', () => {
    const result = presentActivityEvents([{
      id: 'decision', kind: 'decision.orchestrator', timestamp: '2026-07-11T10:00:02Z',
      rawRange: range, decisionType: 'reissue', reason: 'One more pass is needed.',
      action: 'reissue',
    }], 'AGT-2088', null);

    expect(result[0]).toMatchObject({
      kind: 'decision.orchestrator',
      decisionType: 'reissue',
      reason: 'One more pass is needed.',
      action: undefined,
    });
  });

  it('projects a Codex stderr transcript as neutral evidence plus the complete agent answer', () => {
    const events = projectConversation({
      source: 'AGT-2168',
      lines: sanitizeProjectionLines(codexTextModeStderrTranscriptFragment()),
    });

    expect(events.filter((event) =>
      event.kind === 'system.status' && event.label === 'CLI failed')).toHaveLength(0);
    expect(events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'codex-transcript',
      label: 'Codex transcript',
      severity: 'info',
    }));
    expect(events).toContainEqual(expect.objectContaining({
      kind: 'message.taskAgent',
      body: expect.stringContaining('Its second line is preserved in that same turn.'),
    }));
  });

  it('drops legacy completion prose when a typed runner completion exists', () => {
    const result = presentActivityEvents([{
      id: 'done', kind: 'system.status', timestamp: '2026-07-11T10:00:02Z', rawRange: range,
      category: 'result', label: 'Result', explanation: 'Turn completed (tokens: 1200)',
    }], 'AGT-2088', null, { typedTurnCompletions: true });
    expect(result).toEqual([]);
  });

  it('removes free completion transport lines only when typed lifecycle events exist', () => {
    const lines = [
      { timestamp: '2026-07-11T10:00:00Z', stream: 'stdout', text: 'Turn completed (tokens: 1,200)' },
      { timestamp: '2026-07-11T10:00:01Z', stream: 'stdout', text: 'Session completed.' },
      { timestamp: '2026-07-11T10:00:02Z', stream: 'stdout', text: 'Implementation completed the turn safely.' },
    ];

    expect(stripLegacyCompletionLines(lines, true).map(line => line.text)).toEqual([
      'Implementation completed the turn safely.',
    ]);
    expect(stripLegacyCompletionLines(lines, false)).toEqual(lines);
  });
});

// A block of runner status rows repeats the same visual label (RUNNER) four
// times with a different meaning each time, and puts a trace button on every
// row even though there are only a handful of facts. The library renders one
// `<li>` per `system.status` event with no grouping hook, so the fix
// collapses the run into one event at the projection layer; the compatibility
// directive (activity-event-presentation.directive.ts) turns
// `runnerGroupRows` into the nested per-fact disclosure.
describe('presentActivityEvents - runner status grouping', () => {
  function runnerStatus(id: string, label: string, start: number): ConversationEvent {
    return {
      id, kind: 'system.status', timestamp: `2026-07-11T10:00:0${start}Z`,
      rawRange: { source: 'AGT-2088', start, end: start },
      category: 'runner', severity: 'info', label, explanation: `${label} detail`,
    };
  }

  it('collapses four consecutive runner rows into one runner-group event', () => {
    const events = [
      runnerStatus('r1', 'Runner ready', 1),
      runnerStatus('r2', 'Runner started', 2),
      runnerStatus('r3', 'Runner config', 3),
      runnerStatus('r4', 'Runner finished', 4),
    ];
    const result = presentActivityEvents(events, 'AGT-2088', null);
    expect(result).toHaveLength(1);
    const group = result[0] as PresentedRunnerGroupEvent;
    expect(isPresentedRunnerGroupEvent(group)).toBe(true);
    expect(group.category).toBe('runner-group');
    expect(group.label).toBe('Runner');
    expect(group.rawRange).toEqual({ source: 'AGT-2088', start: 1, end: 4 });
    expect(group.runnerGroupRows.map((row) => row.label)).toEqual([
      'Runner ready', 'Runner started', 'Runner config', 'Runner finished',
    ]);
  });

  it('escalates group severity to the worst individual fact', () => {
    const events = [
      runnerStatus('r1', 'Runner ready', 1),
      { ...runnerStatus('r2', 'Runner stopped', 2), severity: 'error' as const },
    ];
    const group = presentActivityEvents(events, 'AGT-2088', null)[0] as PresentedRunnerGroupEvent;
    expect(group.severity).toBe('error');
  });

  it('leaves a lone runner fact ungrouped', () => {
    const result = presentActivityEvents([runnerStatus('r1', 'Runner ready', 1)], 'AGT-2088', null);
    expect(result).toHaveLength(1);
    expect(isPresentedRunnerGroupEvent(result[0])).toBe(false);
    expect((result[0] as SystemStatusEvent).category).toBe('runner');
  });

  it('does not merge runner rows across an unrelated event, and leaves the other event untouched', () => {
    const message: ConversationEvent = {
      id: 'm1', kind: 'message.taskAgent', timestamp: '2026-07-11T10:00:01.5Z',
      rawRange: { source: 'AGT-2088', start: 2, end: 2 }, actor: 'Agent', body: 'Working on it.',
    };
    const events = [runnerStatus('r1', 'Runner ready', 1), message, runnerStatus('r2', 'Runner finished', 3)];
    const result = presentActivityEvents(events, 'AGT-2088', null);
    expect(result).toHaveLength(3);
    expect(result[0]).toMatchObject({ id: 'r1', category: 'runner' });
    expect(result[1]).toEqual(message);
    expect(result[2]).toMatchObject({ id: 'r2', category: 'runner' });
  });
});
