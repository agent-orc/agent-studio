import type {
  ConversationEvent,
  ConversationEventSeverity,
  SystemStatusEvent,
  ToolBurstEvent,
  ToolCommandExecution,
  ToolFamily,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';

/**
 * Codex text-mode emits explicit record headers on stderr. These are protocol
 * discriminators, not display text. Payload classification is based only on
 * this header, so JSON, Markdown and diff contents never need to be guessed.
 */
const CODEX_RECORD_HEADERS = new Map<string, 'agent' | 'user' | 'tool'>([
  ['codex', 'agent'],
  ['user', 'user'],
  ['exec', 'tool'],
  ['apply_patch', 'tool'],
  ['update_plan', 'tool'],
  ['view_image', 'tool'],
  ['web_search', 'tool'],
  ['imagegen', 'tool'],
  ['read_file', 'tool'],
  ['read_mcp_resource', 'tool'],
]);

const MARKUP_FILE_EXTENSION = /\.(?:html?|xhtml|xml|svg)(?:[?#].*)?$/i;
const CODEX_BANNER = /^OpenAI Codex\b/;
const RUNNER_LINE = /^\[runner\]\s*(?<body>.*)$/i;
const RUNNER_DELIVERY = /^\[runner-log-delivery:[^\]]+\]$/i;
const TERMINAL_SENTINEL =
  /\[\[TASK_(?<kind>DONE|NOOP|BLOCKED|NEEDS_INPUT)(?::(?<detail>[^\]]*))?\]\]/gi;
const TOOL_RESULT = /^\s*(?<status>succeeded|failed)\s+in\s+.+:$/i;

type TranscriptMode = 'outside' | 'metadata' | 'agent' | 'user' | 'tool';

interface TranscriptBlock {
  mode: Exclude<TranscriptMode, 'outside' | 'metadata'>;
  header: string;
  start: number;
  lines: { line: CliOutputLine; index: number }[];
}

export interface StructuredActivityProjection {
  /** Lines still owned by the shared coding-agent-chat projector. */
  projectionLines: CliOutputLine[];
  /** Typed records extracted from Codex text-mode and runner system events. */
  events: ConversationEvent[];
}

/**
 * Split structured records out before Markdown projection.
 *
 * The raw Trace remains untouched at the caller. This readable projection
 * consumes only records with explicit stream/header structure:
 * - Codex `exec`/`apply_patch`/... records become collapsible tool events.
 * - Codex `codex` records become agent messages.
 * - `[runner]` records on the system stream become quiet status rows.
 */
export function projectStructuredActivityContent(
  lines: readonly CliOutputLine[],
  source: string,
): StructuredActivityProjection {
  const projectionLines: CliOutputLine[] = [];
  const events: ConversationEvent[] = [];
  const firstCodexHeader = lines.findIndex((line) =>
    line.stream === 'stderr' && CODEX_RECORD_HEADERS.has(line.text.trim()));
  const hasTruncatedCodexTail = firstCodexHeader >= 0
    && !lines.slice(0, firstCodexHeader).some((line) =>
      line.stream === 'stderr' && CODEX_BANNER.test(line.text.trim()));

  let mode: TranscriptMode = hasTruncatedCodexTail ? 'metadata' : 'outside';
  let block: TranscriptBlock | null = null;

  const finishBlock = () => {
    if (!block) return;
    const completed = block;
    block = null;
    if (completed.mode === 'tool') {
      events.push(toolEvent(completed, source));
    } else if (completed.mode === 'agent') {
      events.push(...agentEvents(completed, source));
    }
    // User blocks repeat the task prompt already owned by the detail view (or
    // the durable stream=user message), so the transcript copy is discarded.
  };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const runner = runnerEvent(line, index, source);
    if (runner !== undefined) {
      if (isRunnerExit(line.text)) {
        const finalBlockStart = events.length;
        finishBlock();
        if (runner && !mergeTerminalResultWithExit(events, runner, line.text, finalBlockStart)) {
          events.push(runner);
        }
        mode = 'outside';
      } else if (runner) {
        events.push(runner);
      }
      continue;
    }

    if (line.stream === 'stderr' && CODEX_BANNER.test(line.text.trim())) {
      finishBlock();
      mode = 'metadata';
      continue;
    }

    const header = line.stream === 'stderr'
      ? CODEX_RECORD_HEADERS.get(line.text.trim())
      : undefined;
    if (header && (mode !== 'outside' || index === firstCodexHeader)) {
      finishBlock();
      mode = header;
      block = {
        mode: header,
        header: line.text.trim(),
        start: index,
        lines: [],
      };
      continue;
    }

    if (mode !== 'outside') {
      if (block) block.lines.push({ line, index });
      // Metadata and the leading fragment of a capped transcript are
      // technical evidence. They remain available in Trace, not readable chat.
      continue;
    }

    projectionLines.push(line);
  }

  finishBlock();
  return { projectionLines, events: collapseRunnerRuns(events, source) };
}

/**
 * A contiguous run of `[runner]` bootstrap facts (working tree ready,
 * spawning, spec, config, ...) used to become one `system.status` row per
 * fact, each repeating the same generic "Runner" label and each carrying its
 * own trace affordance in the shared rendering library - six near-identical
 * rows that read as noise even though they state six distinct facts
 * (AGT-2793 second evidence set). Collapse a contiguous run of runner facts
 * into one heading event whose explanation names each fact on its own line,
 * with one merged trace range spanning the whole run, so the group reads as
 * one row instead of six.
 *
 * A run of exactly one fact is left as-is (its existing specific label -
 * "Runner ready", "Runner started", ... - already says what it states).
 * Only a run of two or more collapses, since that is where the repeated
 * generic label became the visible problem.
 */
function collapseRunnerRuns(events: readonly ConversationEvent[], source: string): ConversationEvent[] {
  const out: ConversationEvent[] = [];
  let run: SystemStatusEvent[] = [];

  const flush = () => {
    if (run.length === 0) return;
    if (run.length === 1) {
      out.push(run[0]);
    } else {
      const first = run[0];
      const last = run[run.length - 1];
      out.push({
        ...first,
        label: 'Runner',
        explanation: run.map((fact) => `${fact.label}: ${fact.explanation}`).join('\n'),
        rawRange: { source, start: first.rawRange.start, end: last.rawRange.end },
      });
    }
    run = [];
  };

  for (const event of events) {
    if (event.kind === 'system.status' && event.category === 'runner') {
      run.push(event);
      continue;
    }
    flush();
    out.push(event);
  }
  flush();

  return out;
}

function runnerEvent(
  line: CliOutputLine,
  index: number,
  source: string,
): SystemStatusEvent | null | undefined {
  if (line.stream !== 'system') return undefined;
  if (RUNNER_DELIVERY.test(line.text.trim())) return null;
  const match = RUNNER_LINE.exec(line.text.trim());
  if (!match?.groups) return undefined;

  const body = match.groups['body'].trim();
  const { label, explanation, severity } = runnerPresentation(body);
  return {
    id: `${source}:runner:${index + 1}`,
    kind: 'system.status',
    timestamp: line.timestamp,
    rawRange: { source, start: index + 1, end: index + 1 },
    category: 'runner',
    severity,
    label,
    explanation,
    collapsedByDefault: false,
  };
}

function runnerPresentation(body: string): {
  label: string;
  explanation: string;
  severity: ConversationEventSeverity;
} {
  if (/^working tree ready\b/i.test(body)) {
    return { label: 'Runner ready', explanation: sentence(body), severity: 'info' };
  }
  if (/^spawning\b/i.test(body)) {
    return { label: 'Runner started', explanation: sentence(body), severity: 'info' };
  }
  if (/^spec\b/i.test(body)) {
    return { label: 'Runner config', explanation: sentence(body), severity: 'info' };
  }
  if (/^CLI exited\s+0\b/i.test(body)) {
    return { label: 'Runner finished', explanation: sentence(body), severity: 'info' };
  }
  if (/^CLI exited\b/i.test(body) || /\bfailed\b/i.test(body)) {
    return { label: 'Runner stopped', explanation: sentence(body), severity: 'error' };
  }
  return { label: 'Runner', explanation: sentence(body), severity: 'info' };
}

function toolEvent(block: TranscriptBlock, source: string): ToolBurstEvent {
  const content = trimBlankEdges(block.lines);
  const commandLine = content[0]?.line.text.trim() || block.header;
  const markupFile = markupFilePath(block.header, commandLine);
  const outputLines = content.slice(1).map(({ line }) => line.text);
  const resultLine = outputLines.find((text) => TOOL_RESULT.test(text));
  const result = resultLine ? TOOL_RESULT.exec(resultLine)?.groups?.['status'].toLowerCase() : null;
  const failed = result === 'failed';
  const family = toolFamily(block.header);
  const files = toolFilePaths(block.header, commandLine, content.map(({ line }) => line.text));
  const sample = family === 'edit' && files.length > 0 ? files[0] : commandLine;
  const command: ToolCommandExecution = {
    command: commandLine,
    status: failed ? 'failed' : result === 'succeeded' ? 'completed' : 'unknown',
    exitCode: failed ? 1 : result === 'succeeded' ? 0 : null,
    output: outputLines.join('\n').trimEnd(),
    outputLineCount: outputLines.length,
    outputTruncated: false,
  };
  const end = content.at(-1)?.index ?? block.start;
  return {
    id: `${source}:structured-tool:${block.start + 1}`,
    kind: 'toolBurst',
    timestamp: content[0]?.line.timestamp ?? new Date(0).toISOString(),
    rawRange: { source, start: block.start + 1, end: end + 1 },
    count: 1,
    families: { [family]: 1 },
    failures: failed ? 1 : 0,
    durationMs: durationMs(content),
    samples: { [family]: sample },
    commands: [command],
    files: files.length > 0 ? files : markupFile ? [markupFile] : undefined,
    collapsedByDefault: true,
  };
}

function toolFilePaths(header: string, commandLine: string, lines: readonly string[]): string[] {
  if (header !== 'apply_patch') return [];
  const files: string[] = [];
  const seen = new Set<string>();
  const target = /^\*\*\*\s+(?:Add|Update|Delete)\s+File:\s*(?<path>.+?)\s*$/i;
  const moveTarget = /^\*\*\*\s+Move to:\s*(?<path>.+?)\s*$/i;
  for (const line of [commandLine, ...lines]) {
    const path = (target.exec(line) ?? moveTarget.exec(line))?.groups?.['path']?.trim();
    if (!path || seen.has(path)) continue;
    seen.add(path);
    files.push(path);
  }
  return files;
}

function agentEvents(block: TranscriptBlock, source: string): ConversationEvent[] {
  const content = trimBlankEdges(block.lines);
  if (content.length === 0) return [];
  const rawBody = content.map(({ line }) => line.text).join('\n').trim();
  let terminalKind: string | null = null;
  let terminalDetail = '';
  const body = rawBody.replace(TERMINAL_SENTINEL, (...args: unknown[]) => {
    const groups = args.at(-1) as { kind?: string; detail?: string } | undefined;
    terminalKind = groups?.kind?.toUpperCase() ?? null;
    terminalDetail = groups?.detail?.trim() ?? '';
    return '';
  }).replace(/\n{3,}/g, '\n\n').trim();
  const end = content.at(-1)?.index ?? block.start;
  const range = { source, start: block.start + 1, end: end + 1 };
  const events: ConversationEvent[] = [];

  if (body) {
    events.push({
      id: `${source}:structured-agent:${block.start + 1}`,
      kind: 'message.taskAgent',
      timestamp: content[0].line.timestamp,
      rawRange: range,
      actor: 'Agent',
      body,
    });
  }
  if (terminalKind) {
    events.push({
      id: `${source}:structured-result:${block.start + 1}`,
      kind: 'system.status',
      timestamp: content.at(-1)?.line.timestamp ?? content[0].line.timestamp,
      rawRange: range,
      category: 'result',
      severity: terminalKind === 'BLOCKED' || terminalKind === 'NEEDS_INPUT' ? 'warn' : 'info',
      label: terminalLabel(terminalKind),
      explanation: terminalDetail,
    });
  }
  return events;
}

function terminalLabel(kind: string): string {
  switch (kind) {
    case 'DONE': return 'Task complete';
    case 'NOOP': return 'No action needed';
    case 'BLOCKED': return 'Task blocked';
    case 'NEEDS_INPUT': return 'Input needed';
    default: return 'Task finished';
  }
}

/**
 * A terminal sentinel and the immediately following runner exit describe one
 * outcome. Keep one calm status row, extend its trace range through the exit,
 * and retain only the operator-useful typed outcome and exit code.
 */
function mergeTerminalResultWithExit(
  events: ConversationEvent[],
  runner: SystemStatusEvent,
  rawExit: string,
  finalBlockStart: number,
): boolean {
  let terminalIndex = -1;
  for (let index = events.length - 1; index >= finalBlockStart; index -= 1) {
    const event = events[index];
    if (event.kind === 'system.status' && event.category === 'result') {
      terminalIndex = index;
      break;
    }
  }
  if (terminalIndex < 0) return false;

  const terminal = events[terminalIndex] as SystemStatusEvent;
  const exit = /\bCLI exited\s+(?<code>-?\d+)\b/i.exec(rawExit)?.groups?.['code'] ?? 'unknown';
  const typed = /\btypedOutcome=(?<outcome>[^\s;]+)/i.exec(rawExit)?.groups?.['outcome'] ?? 'unknown';
  const detail = terminal.explanation.trim();
  events[terminalIndex] = {
    ...terminal,
    timestamp: runner.timestamp,
    rawRange: {
      source: terminal.rawRange.source,
      start: terminal.rawRange.start,
      end: runner.rawRange.end,
    },
    explanation: [detail, `Outcome ${typed}`, `Exit ${exit}`].filter(Boolean).join(' · '),
  };
  return true;
}

function toolFamily(header: string): ToolFamily {
  if (header === 'exec') return 'command';
  if (header === 'apply_patch') return 'edit';
  if (header === 'update_plan') return 'todo';
  if (header === 'read_file' || header === 'read_mcp_resource') return 'read';
  return 'other';
}

/**
 * File payloads become tool events from their declared record header, never
 * from looking at the payload body. The extension check only annotates known
 * file-read tools as markup so the canonical tool renderer can disclose the
 * source path beside its collapsed, plain-text <pre>.
 */
function markupFilePath(header: string, commandLine: string): string | null {
  if (header !== 'read_file' && header !== 'read_mcp_resource') return null;
  const path = commandLine.trim().replace(/^['"]|['"]$/g, '');
  return MARKUP_FILE_EXTENSION.test(path) ? path : null;
}

function trimBlankEdges(
  entries: TranscriptBlock['lines'],
): TranscriptBlock['lines'] {
  let start = 0;
  let end = entries.length;
  while (start < end && !entries[start].line.text.trim()) start += 1;
  while (end > start && !entries[end - 1].line.text.trim()) end -= 1;
  return entries.slice(start, end);
}

function durationMs(entries: TranscriptBlock['lines']): number {
  if (entries.length < 2) return 0;
  const start = Date.parse(entries[0].line.timestamp);
  const end = Date.parse(entries.at(-1)?.line.timestamp ?? '');
  return Number.isFinite(start) && Number.isFinite(end) ? Math.max(0, end - start) : 0;
}

function sentence(value: string): string {
  if (!value) return '';
  const normalized = value[0].toUpperCase() + value.slice(1);
  return /[.!?]$/.test(normalized) ? normalized : `${normalized}.`;
}

function isRunnerExit(text: string): boolean {
  return /^\[runner\]\s+CLI exited\b/i.test(text.trim());
}
