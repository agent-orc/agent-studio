import type {
  ConversationEvent,
  ConversationEventSeverity,
  SystemStatusEvent,
  ToolBurstEvent,
  ToolCommandExecution,
  ToolFamily,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';
import {
  aspectSentinelExplanation,
  parseRuntimeSentinels,
  terminalSentinelLabel,
} from '../runtime-sentinel.parser';

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

    const sentinelProjection = sentinelEvents(line.text, line.timestamp, index, source);
    if (sentinelProjection) {
      events.push(...sentinelProjection.events);
      if (sentinelProjection.text) projectionLines.push({ ...line, text: sentinelProjection.text });
    } else {
      projectionLines.push(line);
    }
  }

  finishBlock();
  return { projectionLines, events };
}

function sentinelEvents(
  text: string,
  timestamp: string,
  index: number,
  source: string,
): { text: string; events: SystemStatusEvent[] } | null {
  const parsed = parseRuntimeSentinels(text);
  if (parsed.aspects.length === 0 && parsed.terminals.length === 0) return null;
  const range = { source, start: index + 1, end: index + 1 };
  const events: SystemStatusEvent[] = parsed.aspects.map((aspect, ordinal) => ({
    id: `${source}:aspect:${index + 1}:${ordinal}`,
    kind: 'system.status',
    timestamp,
    rawRange: range,
    category: 'aspect-verdict',
    severity: aspect.status === 'block' ? 'error' : aspect.status === 'concerns' ? 'warn' : 'info',
    label: aspect.status === 'pass' ? 'Passed' : aspect.status === 'concerns' ? 'Passed with concerns' : 'Blocked',
    explanation: aspectSentinelExplanation(aspect),
  }));
  parsed.terminals.forEach((terminal, ordinal) => events.push({
    id: `${source}:terminal:${index + 1}:${ordinal}`,
    kind: 'system.status',
    timestamp,
    rawRange: range,
    category: 'result',
    severity: terminal.kind === 'blocked' || terminal.kind === 'needs-input' ? 'warn' : 'info',
    label: terminalSentinelLabel(terminal),
    explanation: terminal.detail,
  }));
  return { text: parsed.text, events };
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
  const parsed = parseRuntimeSentinels(rawBody);
  const body = parsed.text;
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
  for (const [index, aspect] of parsed.aspects.entries()) {
    const label = aspect.status === 'pass'
      ? 'Passed'
      : aspect.status === 'concerns' ? 'Passed with concerns' : 'Blocked';
    events.push({
      id: `${source}:structured-aspect:${block.start + 1}:${index}`,
      kind: 'system.status',
      timestamp: content.at(-1)?.line.timestamp ?? content[0].line.timestamp,
      rawRange: range,
      category: 'aspect-verdict',
      severity: aspect.status === 'block' ? 'error' : aspect.status === 'concerns' ? 'warn' : 'info',
      label,
      explanation: aspectSentinelExplanation(aspect),
    });
  }
  for (const [index, terminal] of parsed.terminals.entries()) {
    events.push({
      id: `${source}:structured-result:${block.start + 1}:${index}`,
      kind: 'system.status',
      timestamp: content.at(-1)?.line.timestamp ?? content[0].line.timestamp,
      rawRange: range,
      category: 'result',
      severity: terminal.kind === 'blocked' || terminal.kind === 'needs-input' ? 'warn' : 'info',
      label: terminalSentinelLabel(terminal),
      explanation: terminal.detail,
    });
  }
  return events;
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
