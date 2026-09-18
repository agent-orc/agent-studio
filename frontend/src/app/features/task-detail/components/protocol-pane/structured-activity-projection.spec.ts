import { describe, expect, it } from 'vitest';
import type { MessageEvent, SystemStatusEvent, ToolBurstEvent } from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import { projectStructuredActivityContent } from './structured-activity-projection';

function fixture(): CliOutputLine[] {
  const at = (index: number) => `2026-07-28T10:40:${String(index).padStart(2, '0')}.000Z`;
  return [
    { timestamp: at(0), stream: 'system', text: "[runner] working tree ready on branch 'main'" },
    { timestamp: at(1), stream: 'system', text: '[runner] spawning codex exec -m gpt-5.6-sol -' },
    { timestamp: at(2), stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
    { timestamp: at(3), stream: 'stderr', text: 'user' },
    { timestamp: at(4), stream: 'stderr', text: 'Create the concept.' },
    { timestamp: at(5), stream: 'stderr', text: 'codex' },
    { timestamp: at(6), stream: 'stderr', text: 'I will inspect the result.' },
    { timestamp: at(7), stream: 'system', text: '[runner-log-delivery:fixture]' },
    { timestamp: at(8), stream: 'stderr', text: 'exec' },
    { timestamp: at(9), stream: 'stderr', text: '/bin/bash -lc "git diff -- docs/start/README.md"' },
    { timestamp: at(10), stream: 'stderr', text: ' succeeded in 18ms:' },
    { timestamp: at(11), stream: 'stderr', text: 'diff --git a/docs/start/README.md b/docs/start/README.md' },
    { timestamp: at(12), stream: 'stderr', text: '+{' },
    { timestamp: at(13), stream: 'stderr', text: '+  "title": "Apply Robert\'s selected Deck icon",' },
    { timestamp: at(14), stream: 'stderr', text: '+}' },
    { timestamp: at(15), stream: 'stderr', text: 'codex' },
    { timestamp: at(16), stream: 'stderr', text: 'Ready for review.' },
    { timestamp: at(17), stream: 'stderr', text: '[[TASK_DONE]]' },
    { timestamp: at(18), stream: 'system', text: '[runner] CLI exited 0; typedOutcome=ExplicitAgentDone' },
  ];
}

describe('projectStructuredActivityContent', () => {
  it('uses Codex record headers to project payloads as collapsible tool output', () => {
    const result = projectStructuredActivityContent(fixture(), 'AGT-2355');
    const tool = result.events.find((event): event is ToolBurstEvent => event.kind === 'toolBurst');

    expect(tool).toBeDefined();
    expect(tool?.families).toEqual({ command: 1 });
    expect(tool?.collapsedByDefault).toBe(true);
    expect(tool?.commands?.[0]).toMatchObject({
      command: '/bin/bash -lc "git diff -- docs/start/README.md"',
      status: 'completed',
      exitCode: 0,
    });
    expect(tool?.commands?.[0].output).toContain('diff --git a/docs/start/README.md');
    expect(tool?.commands?.[0].output).toContain('"title": "Apply Robert\'s selected Deck icon"');
    expect(result.events.filter((event) => event.kind === 'message.taskAgent'))
      .toEqual([
        expect.objectContaining({ body: 'I will inspect the result.' }),
        expect.objectContaining({ body: 'Ready for review.' }),
      ]);
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'result',
      label: 'Task complete',
      explanation: 'Outcome ExplicitAgentDone · Exit 0',
      rawRange: { source: 'AGT-2355', start: 16, end: 19 },
    }));
    expect(result.events).not.toContainEqual(expect.objectContaining({
      kind: 'system.status',
      label: 'Runner finished',
    }));
    expect(result.projectionLines).toEqual([]);
  });

  it('projects runner system records quietly and drops delivery bookkeeping', () => {
    const result = projectStructuredActivityContent(fixture(), 'AGT-2355');
    const runner = result.events.filter((event): event is SystemStatusEvent =>
      event.kind === 'system.status' && event.category === 'runner');

    expect(runner).toHaveLength(2);
    expect(runner.map((event) => event.label)).toEqual([
      'Runner ready',
      'Runner started',
    ]);
    expect(JSON.stringify(runner)).not.toContain('[runner]');
    expect(JSON.stringify(result.events)).not.toContain('[runner-log-delivery:');
  });

  it('projects aspect and terminal sentinels as status rows without raw marker text', () => {
    const lines: CliOutputLine[] = [
      { timestamp: '2026-09-18T10:00:00Z', stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
      { timestamp: '2026-09-18T10:00:01Z', stream: 'stderr', text: 'codex' },
      { timestamp: '2026-09-18T10:00:02Z', stream: 'stderr', text: 'Review complete.' },
      { timestamp: '2026-09-18T10:00:03Z', stream: 'stderr', text: '[[ASPECT_VERDICT: status=concerns; summary=Dead assertion; evidence_checked=a.spec.ts; missing=none]] [[TASK_DONE]]' },
    ];

    const result = projectStructuredActivityContent(lines, 'AGT-2794');

    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'aspect-verdict',
      label: 'Passed with concerns',
      explanation: expect.stringContaining('Evidence: a.spec.ts'),
    }));
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'result',
      label: 'Task complete',
    }));
    expect(JSON.stringify(result.events)).not.toContain('ASPECT_VERDICT');
    expect(JSON.stringify(result.events)).not.toContain('TASK_DONE');
  });

  it('keeps markup file-tool payloads out of agent Markdown', () => {
    const at = (index: number) => `2026-07-29T22:15:${String(index).padStart(2, '0')}.000Z`;
    const lines: CliOutputLine[] = [
      { timestamp: at(0), stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
      { timestamp: at(1), stream: 'stderr', text: 'codex' },
      { timestamp: at(2), stream: 'stderr', text: 'I will inspect the concept document.' },
      { timestamp: at(3), stream: 'stderr', text: 'read_file' },
      { timestamp: at(4), stream: 'stderr', text: 'docs/concepts/wiki-concept.html' },
      { timestamp: at(5), stream: 'stderr', text: ' succeeded in 12ms:' },
      { timestamp: at(6), stream: 'stderr', text: '<!doctype html>' },
      { timestamp: at(7), stream: 'stderr', text: '<meta charset="utf-8">' },
      { timestamp: at(8), stream: 'stderr', text: '<p class="lead">Readable concept</p>' },
      { timestamp: at(9), stream: 'stderr', text: '.card {' },
      { timestamp: at(10), stream: 'stderr', text: '  display: grid;' },
      { timestamp: at(11), stream: 'stderr', text: '}' },
      { timestamp: at(12), stream: 'stderr', text: 'codex' },
      { timestamp: at(13), stream: 'stderr', text: 'The document is ready.' },
    ];

    const result = projectStructuredActivityContent(lines, 'AGT-2433');
    const tool = result.events.find((event): event is ToolBurstEvent =>
      event.kind === 'toolBurst' && event.families.read === 1);
    const agentBodies = result.events
      .filter((event): event is MessageEvent => event.kind === 'message.taskAgent')
      .map(event => event.body);

    expect(tool).toMatchObject({
      collapsedByDefault: true,
      families: { read: 1 },
      files: ['docs/concepts/wiki-concept.html'],
    });
    expect(tool?.commands?.[0]).toMatchObject({
      command: 'docs/concepts/wiki-concept.html',
      status: 'completed',
    });
    expect(tool?.commands?.[0].output).toContain('<meta charset="utf-8">');
    expect(agentBodies).toEqual([
      'I will inspect the concept document.',
      'The document is ready.',
    ]);
    expect(agentBodies.join('\n')).not.toContain('<p class=');
  });

  it('annotates apply_patch records with their unique edit targets', () => {
    const root = 'C:\\Temp\\ass-worktrees\\fixture\\AGT-2526';
    const lines: CliOutputLine[] = [
      { timestamp: '2026-08-09T10:00:00.000Z', stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
      { timestamp: '2026-08-09T10:00:01.000Z', stream: 'stderr', text: 'apply_patch' },
      { timestamp: '2026-08-09T10:00:02.000Z', stream: 'stderr', text: '*** Begin Patch' },
      { timestamp: '2026-08-09T10:00:03.000Z', stream: 'stderr', text: `*** Update File: ${root}\\frontend\\src\\app\\campaign.ts` },
      { timestamp: '2026-08-09T10:00:04.000Z', stream: 'stderr', text: `*** Update File: ${root}\\frontend\\src\\app\\campaign.ts` },
      { timestamp: '2026-08-09T10:00:05.000Z', stream: 'stderr', text: `*** Add File: ${root}\\frontend\\src\\app\\campaign.spec.ts` },
      { timestamp: '2026-08-09T10:00:06.000Z', stream: 'stderr', text: '*** End Patch' },
      { timestamp: '2026-08-09T10:00:07.000Z', stream: 'stderr', text: ' succeeded in 24ms:' },
      { timestamp: '2026-08-09T10:00:08.000Z', stream: 'stderr', text: 'Done!' },
    ];

    const result = projectStructuredActivityContent(lines, 'AGT-2526');
    const tool = result.events.find((event): event is ToolBurstEvent => event.kind === 'toolBurst');

    expect(tool).toMatchObject({
      families: { edit: 1 },
      samples: { edit: `${root}\\frontend\\src\\app\\campaign.ts` },
      files: [
        `${root}\\frontend\\src\\app\\campaign.ts`,
        `${root}\\frontend\\src\\app\\campaign.spec.ts`,
      ],
    });
  });

  it('recognizes XML-family resource payloads from their tool header and extension', () => {
    const lines: CliOutputLine[] = [
      { timestamp: '2026-07-29T22:16:00.000Z', stream: 'stderr', text: 'OpenAI Codex v0.144.1' },
      { timestamp: '2026-07-29T22:16:01.000Z', stream: 'stderr', text: 'read_mcp_resource' },
      { timestamp: '2026-07-29T22:16:02.000Z', stream: 'stderr', text: 'skill://wiki/diagram.svg' },
      { timestamp: '2026-07-29T22:16:03.000Z', stream: 'stderr', text: '<svg><path d="M0 0"/></svg>' },
    ];

    const result = projectStructuredActivityContent(lines, 'AGT-2433');

    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'toolBurst',
      families: { read: 1 },
      files: ['skill://wiki/diagram.svg'],
      collapsedByDefault: true,
    }));
    expect(result.events.some(event => event.kind === 'message.taskAgent')).toBe(false);
  });

  it('drops an unowned leading fragment when a capped buffer starts inside a tool payload', () => {
    const tail = fixture().slice(10);
    const result = projectStructuredActivityContent(tail, 'AGT-2355');

    expect(JSON.stringify(result.events)).not.toContain('succeeded in 18ms');
    expect(JSON.stringify(result.events)).not.toContain('diff --git');
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'message.taskAgent',
      body: 'Ready for review.',
    }));
  });

  it('does not merge an older sentinel when the final agent block has no terminal outcome', () => {
    const lines = fixture();
    lines.splice(lines.length - 1, 0,
      { timestamp: '2026-07-28T10:40:18.000Z', stream: 'stderr', text: 'codex' },
      { timestamp: '2026-07-28T10:40:19.000Z', stream: 'stderr', text: 'One more note.' },
    );
    lines[lines.length - 1] = {
      timestamp: '2026-07-28T10:40:20.000Z',
      stream: 'system',
      text: '[runner] CLI exited 0; typedOutcome=CleanExitWithoutExplicitOutcome',
    };

    const result = projectStructuredActivityContent(lines, 'AGT-2355');
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'result',
      label: 'Task complete',
    }));
    expect(result.events).toContainEqual(expect.objectContaining({
      kind: 'system.status',
      category: 'runner',
      label: 'Runner finished',
    }));
  });
});
