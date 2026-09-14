// Regression coverage for the host-side conversation-projection guard.
//
// The guard is the app's defense-in-depth against raw stream-json transport
// frames leaking into the chat (bug: "Activity-Log zeigt rohes JSON statt
// Nutzer-Nachrichten"). These specs pin a CATALOG of current stream-json event
// shapes - including thinking blocks with a signature, tool_use, and the
// several tool_result forms - and assert that:
//   1. every transport frame is redacted to the "[internal event]" marker,
//   2. the raw JSON survives on `internalDetail` (disclosable, never inline),
//   3. a brand-new / unknown event shape does NOT fall back to raw rendering,
//   4. ordinary prose and non-transport JSON snippets are left untouched, and
//   5. Codex JSONL frames (owned by the library) are never redacted.
//
// This spec imports only the guard + the app model, so it runs standalone
// without the coding-agent-chat library.
import { describe, expect, it } from 'vitest';
import {
  INTERNAL_EVENT_MARKER,
  isInternalEventLine,
  isNonRenderableRawLine,
  isRenderableActivityKind,
  isTruncatedJsonLine,
  repairTruncatedCodexFrame,
  sanitizeProjectionLines,
  TRUNCATED_PAYLOAD_NOTE,
} from './conversation-projection';
import { CliOutputLine } from '../../../models/task.model';

function line(text: string, stream = 'stdout', timestamp = '2026-07-09T03:15:00.000Z'): CliOutputLine {
  return { timestamp, stream, text };
}

// A catalog of raw stream-json frames as they appear on a single stdout line
// when the backend renderer did not (or could not) turn them into a marker.
const STREAM_JSON_CATALOG: readonly { name: string; raw: string }[] = [
  {
    name: 'assistant text frame',
    raw: '{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"hello world"}]}}',
  },
  {
    name: 'assistant tool_use frame',
    raw: '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.ts"}}]}}',
  },
  {
    name: 'thinking block with signature',
    raw: '{"type":"thinking","thinking":"Let me reason about this.","signature":"Er8BCkgI...=="}',
  },
  {
    name: 'user message wrapping a tool_result (string content)',
    raw: '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"file body"}]}}',
  },
  {
    name: 'user message wrapping a tool_result (array content, is_error)',
    raw: '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"boom"}]}]}}',
  },
  {
    name: 'bare tool_result frame',
    raw: '{"type":"tool_result","tool_use_id":"toolu_3","content":[{"type":"text","text":"ok"}]}',
  },
  {
    name: 'system init frame',
    raw: '{"type":"system","subtype":"init","session_id":"sess-abc","tools":["Read","Bash"]}',
  },
  {
    name: 'result frame',
    raw: '{"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":10}}',
  },
  {
    name: 'rate_limit_event frame',
    raw: '{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1777393800}}',
  },
  {
    name: 'content_block_delta streaming frame',
    raw: '{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial"}}',
  },
  {
    name: 'bare content-block array',
    raw: '[{"type":"text","text":"a"},{"type":"tool_use","id":"t","name":"Read","input":{}}]',
  },
  {
    name: 'message envelope without an explicit type',
    raw: '{"role":"assistant","content":[{"type":"text","text":"hi"}]}',
  },
];

// The forward-compat case: an event type this code has never seen. It must be
// redacted (it still carries transport structure), never rendered raw.
const UNKNOWN_FUTURE_FRAMES: readonly { name: string; raw: string }[] = [
  {
    name: 'unknown future event type carrying a message envelope',
    raw: '{"type":"web_search_tool_result","message":{"role":"assistant","content":[{"type":"text","text":"x"}]}}',
  },
  {
    name: 'unknown future event type carrying a content array',
    raw: '{"type":"server_tool_use","content":[{"type":"text","text":"y"}],"stop_reason":"end_turn"}',
  },
  {
    name: 'unknown future event type carrying a signature',
    raw: '{"type":"redacted_reasoning","signature":"AbCd=="}',
  },
];

describe('sanitizeProjectionLines - ANSI output', () => {
  it('strips terminal colours from ordinary CLI snippets before rendering', () => {
    expect(sanitizeProjectionLines([
      line('\u001b[33m[39m Building...\u001b[0m', 'stderr'),
    ])).toEqual([
      line(' Building...', 'stdout'),
    ]);
  });

  it('keeps ordinary agent stderr transcript lines neutral', () => {
    expect(sanitizeProjectionLines([
      line('- Added the regression coverage.', 'stderr'),
      line('tokens used', 'stderr'),
      line('60,162', 'stderr'),
    ])).toEqual([
      line('- Added the regression coverage.', 'stdout'),
      line('tokens used', 'stdout'),
      line('60,162', 'stdout'),
    ]);
  });

  it('keeps genuine CLI failures on stderr', () => {
    const failure = line('Error: command exited with code 1', 'stderr');
    const input = [failure];
    expect(sanitizeProjectionLines(input)).toBe(input);
  });

  it('preserves a Codex text-mode stderr transcript for the library projector', () => {
    const input = [
      line('[runner] spawning codex exec system marker', 'system'),
      line('OpenAI Codex v0.144.1 (research preview)', 'stderr'),
      line('Reasoning: inspect the Activity feed.', 'stderr'),
      line('Process exited with code 1', 'stderr'),
      line('tokens used', 'stderr'),
      line('60,162', 'stderr'),
      line('The complete agent answer follows on stdout.', 'stdout'),
    ];

    expect(sanitizeProjectionLines(input)).toBe(input);
  });

  it('ends the Codex transcript envelope when a supervisor event takes over', () => {
    expect(sanitizeProjectionLines([
      line('OpenAI Codex v0.144.1 (research preview)', 'stderr'),
      line('Reasoning: inspect the Activity feed.', 'stderr'),
      line(
        '[19:23:30.120] [supervisor] [escalate] Auto-review completion gate stayed open.',
        'stderr',
      ),
      line('Normal agent output after the supervisor event.', 'stderr'),
    ])).toEqual([
      line('OpenAI Codex v0.144.1 (research preview)', 'stderr'),
      line('Reasoning: inspect the Activity feed.', 'stderr'),
      line('**Escalate** · Auto-review completion gate stayed open.', 'supervisor'),
      line('Normal agent output after the supervisor event.', 'stdout'),
    ]);
  });

  it('turns timestamped supervisor output into a structured supervisor message', () => {
    expect(sanitizeProjectionLines([
      line(
        '[19:23:30.120] [supervisor] [escalate] Auto-review completion gate could not clear unfinished work.',
        'stderr',
      ),
    ])).toEqual([
      line(
        '**Escalate** · Auto-review completion gate could not clear unfinished work.',
        'supervisor',
      ),
    ]);
  });
});

describe('isNonRenderableRawLine — stream-json catalog', () => {
  for (const { name, raw } of STREAM_JSON_CATALOG) {
    it(`flags a ${name}`, () => {
      expect(isNonRenderableRawLine(raw)).toBe(true);
    });
  }

  for (const { name, raw } of UNKNOWN_FUTURE_FRAMES) {
    it(`flags an ${name} (no raw fallback for new shapes)`, () => {
      expect(isNonRenderableRawLine(raw)).toBe(true);
    });
  }
});

describe('isNonRenderableRawLine — false-positive guard', () => {
  const RENDERABLE: readonly { name: string; text: string }[] = [
    { name: 'plain agent prose', text: 'Looking at the activity-log component now.' },
    { name: 'a Claude marker line', text: '● Read prompt.md' },
    { name: 'a tool action line', text: '* Edit src/foo.ts' },
    { name: 'a non-transport JSON config snippet', text: '{"port":5030,"host":"localhost"}' },
    { name: 'a JSON snippet with an unrelated type', text: '{"type":"chart","value":42}' },
    { name: 'a prose line that merely mentions JSON', text: 'The frame was {"type":"assistant"} shaped.' },
    { name: 'an envelope with a non-message role', text: '{"role":"admin","content":"x"}' },
    { name: 'an empty object', text: '{}' },
    { name: 'a bare number-array', text: '[1,2,3]' },
    { name: 'an incomplete/partial JSON fragment', text: '{"type":"assistant",' },
  ];
  for (const { name, text } of RENDERABLE) {
    it(`does not flag ${name}`, () => {
      expect(isNonRenderableRawLine(text)).toBe(false);
    });
  }

  it('leaves Codex JSONL frames for the library to parse', () => {
    expect(isNonRenderableRawLine('{"type":"item.completed","item":{"type":"command_execution","id":"c1","command":"ls","exit_code":0}}')).toBe(false);
    expect(isNonRenderableRawLine('{"type":"item.started","item":{"type":"agent_message","text":"hi"}}')).toBe(false);
    expect(isNonRenderableRawLine('{"type":"turn.completed","usage":{"input_tokens":1}}')).toBe(false);
  });
});

describe('sanitizeProjectionLines', () => {
  it('replaces every raw frame with the compact marker and preserves the raw JSON', () => {
    const raw = STREAM_JSON_CATALOG[0].raw;
    const [out] = sanitizeProjectionLines([line(raw)]);
    expect(out.text).toBe(INTERNAL_EVENT_MARKER);
    expect(out.internalDetail).toBe(raw);
    expect(isInternalEventLine(out)).toBe(true);
  });

  it('never emits raw stream-json as visible text for any catalog shape', () => {
    const lines = STREAM_JSON_CATALOG.map((c) => line(c.raw));
    for (const out of sanitizeProjectionLines(lines)) {
      expect(out.text).toBe(INTERNAL_EVENT_MARKER);
      expect(out.text).not.toContain('"type"');
      expect(out.text).not.toContain('{');
    }
  });

  it('collapses a run of consecutive frames into one marker with joined detail', () => {
    const a = STREAM_JSON_CATALOG[0].raw;
    const b = STREAM_JSON_CATALOG[1].raw;
    const out = sanitizeProjectionLines([line(a), line(b)]);
    expect(out).toHaveLength(1);
    expect(out[0].text).toBe(INTERNAL_EVENT_MARKER);
    expect(out[0].internalDetail).toContain(a);
    expect(out[0].internalDetail).toContain(b);
  });

  it('keeps real messages between frames intact and starts a fresh marker each run', () => {
    const frame = STREAM_JSON_CATALOG[2].raw;
    const out = sanitizeProjectionLines([
      line(frame),
      line('real agent message'),
      line(frame),
    ]);
    expect(out.map((l) => l.text)).toEqual([
      INTERNAL_EVENT_MARKER,
      'real agent message',
      INTERNAL_EVENT_MARKER,
    ]);
  });

  it('preserves user and orchestrator lines verbatim', () => {
    const user = line('please continue', 'user');
    const orch = line('[reissue] follow-up', 'orchestrator');
    const out = sanitizeProjectionLines([user, orch]);
    expect(out[0]).toBe(user);
    expect(out[1]).toBe(orch);
  });

  it('returns the same array reference when nothing needs redacting (no churn)', () => {
    const input = [line('hello'), line('● Read a.ts')];
    expect(sanitizeProjectionLines(input)).toBe(input);
  });

  it('removes every Codex todo-list snapshot from the readable projection', () => {
    const started = line('{"type":"item.started","item":{"id":"item_1","type":"todo_list","items":[{"text":"Inspect","completed":false}]}}');
    const updated = line('{"type":"item.updated","item":{"id":"item_1","type":"todo_list","items":[{"text":"Inspect","completed":true}]}}');
    const message = line('{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Done"}}');

    const out = sanitizeProjectionLines([started, updated, message]);

    expect(out).toEqual([message]);
  });
});

describe('sanitizeProjectionLines - protocol-novelty unknown-frame grouping (AGT-2793/AGT-2814)', () => {
  // Fixture: the runner emits one `[runner-protocol-unknown-frame]` marker
  // per occurrence of an unmapped `tool_progress` frame from the `claude`
  // adapter (mirrors the AGT-2793 activity stream: four unknown frames of
  // the same kind interleaved with readable rows), followed by the readable
  // `RUNNER FINISHED` / `RUNNER READY` rows the CLI also emitted.
  function noveltyMarker(occurrence: number, total: number): string {
    return '[runner-protocol-unknown-frame] ' + JSON.stringify({
      cli: 'claude',
      adapterVersion: '0.7.0',
      frameType: 'tool_progress',
      occurrence,
      totalUnknownFrames: total,
      payloadSha256: 'a480e2da5e52776eac9c96b85d7d6a7f'.padEnd(64, '0'),
    });
  }

  it('groups four unknown frames of the same kind into one row with a count, leaving known rows unchanged', () => {
    const known1 = line('[runner] RUNNER READY working tree ready', 'system');
    const known2 = line('[runner] RUNNER FINISHED CLI exited 0', 'system');
    const input = [
      known1,
      line(noveltyMarker(1, 1)),
      line(noveltyMarker(2, 2)),
      line(noveltyMarker(3, 3)),
      line(noveltyMarker(4, 4)),
      known2,
    ];

    const out = sanitizeProjectionLines(input);

    // One grouped row for the four unknown frames, plus the two known rows.
    expect(out).toHaveLength(3);
    expect(out[0]).toBe(known1);
    expect(out[2]).toBe(known2);

    const grouped = out[1];
    expect(grouped.text).toContain('4');
    expect(grouped.text).toContain('tool_progress');
    expect(grouped.text).toContain('claude');
    expect(grouped.text).toContain('0.7.0');
    expect(grouped.text).not.toContain('{');
    expect(grouped.text).not.toContain('payloadSha256');

    // The raw payloads stay available behind disclosure, never inline.
    expect(grouped.internalDetail).toContain('occurrence');
    expect(grouped.internalDetail?.match(/runner-protocol-unknown-frame/g)).toHaveLength(4);

    // No bare/empty rows.
    for (const outLine of out) {
      expect(outLine.text.trim().length).toBeGreaterThan(0);
    }
  });

  it('groups non-consecutive occurrences of the same (cli, adapterVersion, frameType) into one row', () => {
    const out = sanitizeProjectionLines([
      line(noveltyMarker(1, 1)),
      line('real agent message'),
      line(noveltyMarker(2, 2)),
    ]);

    expect(out).toHaveLength(2);
    expect(out[0].text).toContain('2');
    expect(out[1]).toEqual(line('real agent message'));
  });

  it('keeps distinct frame types in separate grouped rows', () => {
    const otherFrameMarker = '[runner-protocol-unknown-frame] ' + JSON.stringify({
      cli: 'claude',
      adapterVersion: '0.7.0',
      frameType: 'future_frame',
      occurrence: 1,
      totalUnknownFrames: 2,
      payloadSha256: 'b'.repeat(64),
    });

    const out = sanitizeProjectionLines([
      line(noveltyMarker(1, 1)),
      line(otherFrameMarker),
    ]);

    expect(out).toHaveLength(2);
    expect(out[0].text).toContain('tool_progress');
    expect(out[1].text).toContain('future_frame');
  });
});

describe('renderable-kind whitelist', () => {
  it('accepts every known activity-log kind', () => {
    for (const kind of ['read', 'search', 'command', 'edit', 'task', 'todo', 'error', 'message', 'orchestrator', 'supervisor', 'other']) {
      expect(isRenderableActivityKind(kind)).toBe(true);
    }
  });

  it('rejects an unknown kind', () => {
    expect(isRenderableActivityKind('stream-json')).toBe(false);
    expect(isRenderableActivityKind('')).toBe(false);
  });
});

// A Codex `item.completed` command frame as it appears on one stdout line. The
// command carries nested escaped quotes, the output a `\u00fc` escape, a
// newline escape, and an escaped backslash, so a cut can land inside each.
const CODEX_COMMAND_FRAME =
  '{"type":"item.completed","item":{"id":"item_13","type":"command_execution",'
  + '"command":"/bin/bash -lc \\"rg -n \\\\\\"CAR migration|Freigabe\\\\\\" --glob \'!**/bin/**\'\\"",'
  + '"aggregated_output":"docs/a.md:1: Schl\\u00fcssel line\\ndocs/b.md:2: second line with a backslash \\\\ here",'
  + '"exit_code":0,"status":"completed"}}';
// Marker the backend CliOutputLogParser appends (number formatted by the host locale).
const BACKEND_CUT_MARKER = '\u2026[truncated: line exceeded 65.536 chars]';
// Marker the remote runner LogShipper appends.
const RUNNER_CUT_MARKER = ' [runner: event payload truncated]';

function cutFrame(at: number, marker = BACKEND_CUT_MARKER): string {
  return CODEX_COMMAND_FRAME.slice(0, at) + marker;
}

function parseRepaired(text: string): { type: string; item: Record<string, unknown> } {
  const repaired = repairTruncatedCodexFrame(text);
  expect(repaired).not.toBeNull();
  return JSON.parse(repaired as string) as { type: string; item: Record<string, unknown> };
}

describe('repairTruncatedCodexFrame - frames cut at the 64 KiB log line cap', () => {
  it('closes a frame cut inside aggregated_output and keeps id, command and type', () => {
    const frame = parseRepaired(cutFrame(CODEX_COMMAND_FRAME.indexOf('second line')));
    expect(frame.type).toBe('item.completed');
    expect(frame.item['id']).toBe('item_13');
    expect(frame.item['type']).toBe('command_execution');
    expect(String(frame.item['command'])).toContain('rg -n \\"CAR migration|Freigabe\\"');
    expect(String(frame.item['aggregated_output'])).toContain('docs/a.md:1: Schlüssel line');
    expect(String(frame.item['aggregated_output'])).toContain(TRUNCATED_PAYLOAD_NOTE);
    expect(frame.item['exit_code']).toBeUndefined();
  });

  it('accepts the runner marker as well', () => {
    const frame = parseRepaired(cutFrame(CODEX_COMMAND_FRAME.indexOf('second line'), RUNNER_CUT_MARKER));
    expect(frame.item['id']).toBe('item_13');
  });

  it('drops an escape sequence the cut left unfinished', () => {
    const insideUnicode = CODEX_COMMAND_FRAME.indexOf('\\u00fc') + 3;
    expect(String(parseRepaired(cutFrame(insideUnicode)).item['aggregated_output'])).toContain('Schl');
    const afterBackslash = CODEX_COMMAND_FRAME.indexOf('\\\\ here') + 1;
    expect(String(parseRepaired(cutFrame(afterBackslash)).item['aggregated_output'])).toContain('backslash');
  });

  it('puts the note on the output when the cut sits between tokens', () => {
    const afterComma = CODEX_COMMAND_FRAME.indexOf('"exit_code"');
    const frame = parseRepaired(cutFrame(afterComma));
    expect(String(frame.item['aggregated_output'])).toContain('second line with a backslash \\ here');
    expect(String(frame.item['aggregated_output'])).toContain(TRUNCATED_PAYLOAD_NOTE);
    expect(frame.item['exit_code']).toBeUndefined();
  });

  it('yields a parseable frame with the original type for every cut position', () => {
    const typeEnd = CODEX_COMMAND_FRAME.indexOf(',');
    const idEnd = CODEX_COMMAND_FRAME.indexOf('"type":"command_execution"');
    for (let at = typeEnd; at < CODEX_COMMAND_FRAME.length; at++) {
      const repaired = repairTruncatedCodexFrame(cutFrame(at));
      expect(repaired, `cut at ${at}`).not.toBeNull();
      const frame = JSON.parse(repaired as string) as { type: string; item?: Record<string, unknown> };
      expect(frame.type, `cut at ${at}`).toBe('item.completed');
      if (at > idEnd) expect(frame.item?.['id'], `cut at ${at}`).toBe('item_13');
    }
  });

  it('leaves intact frames, prose and non-Codex frames alone', () => {
    expect(repairTruncatedCodexFrame(CODEX_COMMAND_FRAME)).toBeNull();
    expect(repairTruncatedCodexFrame('plain prose' + RUNNER_CUT_MARKER)).toBeNull();
    expect(repairTruncatedCodexFrame('{"type":"assistant","message":{"role":"assistant"' + RUNNER_CUT_MARKER)).toBeNull();
    expect(isTruncatedJsonLine('{"type":"assistant","message":{' + RUNNER_CUT_MARKER)).toBe(true);
    expect(isTruncatedJsonLine('plain prose' + RUNNER_CUT_MARKER)).toBe(false);
    expect(isTruncatedJsonLine(CODEX_COMMAND_FRAME)).toBe(false);
  });
});

describe('sanitizeProjectionLines - truncated frames', () => {
  it('hands a rebuilt Codex frame to the projection instead of raw prose', () => {
    const [out] = sanitizeProjectionLines([line(cutFrame(CODEX_COMMAND_FRAME.indexOf('second line')))]);
    expect(out.text).not.toBe(INTERNAL_EVENT_MARKER);
    expect(out.text.startsWith('{"type":"item.completed"')).toBe(true);
    const frame = JSON.parse(out.text) as { item: Record<string, unknown> };
    expect(frame.item['id']).toBe('item_13');
    expect(String(frame.item['aggregated_output'])).toContain(TRUNCATED_PAYLOAD_NOTE);
  });

  it('collapses a cut Anthropic frame to the internal-event marker', () => {
    const raw = '{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"hel'
      + RUNNER_CUT_MARKER;
    const [out] = sanitizeProjectionLines([line(raw)]);
    expect(out.text).toBe(INTERNAL_EVENT_MARKER);
    expect(out.internalDetail).toContain('"type":"assistant"');
  });

  it('leaves an intact Codex frame untouched (same array reference)', () => {
    const input = [line(CODEX_COMMAND_FRAME)];
    expect(sanitizeProjectionLines(input)).toBe(input);
  });
});
