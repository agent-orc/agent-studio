/**
 * Conversation-projection guard (host-side, pure, library-free).
 *
 * Why this exists
 * ---------------
 * The canonical conversation projection (`parseActivityLog`,
 * `buildConversationTurns`, `projectConversation`) lives in the
 * `coding-agent-chat` library. That projection classifies every unrecognised
 * agent stdout line into a `message` group whose title is the *raw line text*
 * (see the library's `activity-log.parser.ts` fallback branch). When a CLI
 * emits a `stream-json` transport frame that the backend renderer did not
 * turn into a marker line - a new Anthropic event shape, a frame that bypassed
 * the Claude renderer, a partial/duplicated frame - the raw JSON reaches the
 * chat and is rendered verbatim (bug: "Activity-Log zeigt rohes JSON statt
 * Nutzer-Nachrichten").
 *
 * The host owns the seam between the polled CLI output buffer and the
 * projection. This guard sits on that seam: it enforces a WHITELIST of
 * renderable line shapes and guarantees that a raw Anthropic stream-json
 * transport frame is NEVER handed to the projection as chat content. Unknown
 * transport shapes (including brand-new event types we have never seen) are
 * collapsed to a single compact `[internal event]` marker line; the original
 * raw JSON is preserved on {@link CliOutputLine.internalDetail} so the Trace /
 * Verbose-Debug surfaces can still disclose it on demand. It is dropped from
 * the readable chat, never rendered as raw text.
 *
 * This is a defense-in-depth layer. The definitive classification also belongs
 * in the library projection (`conversation-projection.ts` +
 * `conversation-projection.spec.ts` in `C:\Projects\coding-agent-chat`); this
 * host guard makes the app robust regardless of the library version installed.
 *
 * Codex note: the library legitimately parses Codex JSONL frames
 * (`{"type":"item.completed","item":{...}}`) into real tool / message groups.
 * Those are NOT redacted here - the guard only recognises Anthropic
 * `stream-json` / Messages-API transport frames.
 *
 * Truncated frames: the log pipeline caps a physical line at 64 KiB (remote
 * runner `LogShipper`, backend `CliOutputLogParser`) and appends a marker. A
 * Codex `item.completed` frame with a large `aggregated_output` loses its
 * closing quotes and braces that way, the library cannot parse it, and the raw
 * JSON used to land in the chat as agent prose (AGT-2373, 2026-09-06). The
 * guard rebuilds such a frame into valid JSON with a visible note so it still
 * projects as a tool call; a cut frame it cannot rebuild collapses to the
 * `[internal event]` marker like any other unparseable transport frame.
 */
import type { CliOutputLine } from '../../../models/task.model';
import { stripAnsi } from '../../../utils/ansi-text';

/** Compact, English (per AGENTS.md) placeholder shown in place of a raw frame. */
export const INTERNAL_EVENT_MARKER = '[internal event]';

/**
 * Prefix the backend `ProtocolNoveltyTelemetry.ToMarker()` puts on a diagnostic
 * line for a provider frame the installed CLI adapter could not classify (see
 * `contracts/TaskServer.Contracts/ProtocolNoveltyContracts.cs`). The payload
 * after the prefix is JSON, but the line as a whole is `"prefix {json}"`, not a
 * complete JSON value, so the generic bracket-matched {@link
 * isNonRenderableRawLine} check never recognises it - it must be matched on
 * this literal prefix instead.
 */
const PROTOCOL_NOVELTY_MARKER_PREFIX = '[runner-protocol-unknown-frame] ';

export interface ProtocolNoveltyMarker {
  cli: string;
  adapterVersion: string;
  frameType: string;
  occurrence: number;
  totalUnknownFrames: number;
  payloadSha256: string;
}

/** JS mirror of `ProtocolNoveltyTelemetry.TryParseMarker`, kept library-free. */
export function parseProtocolNoveltyMarker(text: string | undefined | null): ProtocolNoveltyMarker | null {
  if (!text || !text.startsWith(PROTOCOL_NOVELTY_MARKER_PREFIX)) return null;
  try {
    const parsed = JSON.parse(text.slice(PROTOCOL_NOVELTY_MARKER_PREFIX.length)) as Partial<ProtocolNoveltyMarker>;
    if (
      !parsed
      || typeof parsed.cli !== 'string' || !parsed.cli
      || typeof parsed.adapterVersion !== 'string' || !parsed.adapterVersion
      || typeof parsed.frameType !== 'string' || !parsed.frameType
      || typeof parsed.occurrence !== 'number' || parsed.occurrence < 1
      || typeof parsed.totalUnknownFrames !== 'number' || parsed.totalUnknownFrames < parsed.occurrence
      || typeof parsed.payloadSha256 !== 'string' || parsed.payloadSha256.length !== 64
    ) return null;
    return parsed as ProtocolNoveltyMarker;
  } catch {
    return null;
  }
}

/**
 * Anthropic `stream-json` event `type` values that unambiguously identify a
 * transport frame. A JSON object on stdout carrying one of these is always a
 * frame, never chat prose, so it is redacted on the type alone.
 */
const STRONG_STREAM_JSON_TYPES: ReadonlySet<string> = new Set([
  'assistant',
  'tool_use',
  'tool_result',
  'thinking',
  'redacted_thinking',
  'rate_limit_event',
  'message_start',
  'message_delta',
  'message_stop',
  'content_block_start',
  'content_block_delta',
  'content_block_stop',
  'input_json_delta',
  'signature_delta',
]);

/**
 * Event `type` values whose word is generic enough to also appear in unrelated
 * JSON (`system`, `user`, `result`, `error`). These are treated as transport
 * frames only when a Messages-shape sibling key is also present, so an
 * unrelated `{"type":"error","code":5}` snippet is left alone.
 */
const WEAK_STREAM_JSON_TYPES: ReadonlySet<string> = new Set([
  'system',
  'user',
  'result',
  'error',
]);

/**
 * Sibling keys that mark an object as an Anthropic Messages / stream-json
 * transport frame even when its `type` is unknown (a brand-new event shape) or
 * one of the {@link WEAK_STREAM_JSON_TYPES}.
 */
const STREAM_JSON_SHAPE_KEYS: readonly string[] = [
  'message',
  'content',
  'delta',
  'signature',
  'tool_use_id',
  'session_id',
  'subtype',
  'is_error',
  'stop_reason',
  'usage',
];

/** Roles a bare `{ role, content }` Messages envelope may carry. */
const MESSAGE_ENVELOPE_ROLES: ReadonlySet<string> = new Set(['user', 'assistant', 'system']);

const SUPERVISOR_ENVELOPE =
  /^\[(?<clock>\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?)\]\s+\[supervisor\]\s+\[(?<action>[^\]]+)\]\s*(?<body>.*)$/i;

const GENUINE_CLI_ERROR_PATTERNS: readonly RegExp[] = [
  /^\s*(?:error|exception|fatal|panic)(?:\b|:)/i,
  /^\s*(?:npm|pnpm|yarn)\s+err!/i,
  /^\s*\[force-fail\]/i,
  /\btraceback \(most recent call last\)/i,
  /\berror_during_execution\b/i,
  /"is_error"\s*:\s*true/i,
  /\b(?:cli|command|process)\s+(?:failed|exited)\s+(?:with\s+)?(?:code|status)\s+[1-9]\d*\b/i,
  /\bexit(?:ed)?\s+(?:code|status)\s*[=:]?\s*[1-9]\d*\b/i,
];

const CODEX_EXEC_RUNNER_MARKER = /^\s*\[runner\]\s+spawning\s+codex\s+exec\b/i;
const CODEX_TEXT_MODE_BANNER = /^\s*OpenAI Codex\b/i;

/**
 * Codex JSONL frame `type` prefixes. The library owns these (it maps them to
 * tool / message groups), so the guard must never redact them.
 */
const CODEX_FRAME_TYPE_PREFIXES: readonly string[] = ['item.', 'turn.', 'thread.', 'session.'];

/**
 * The whitelist of activity-log group kinds the UI knows how to render. Kept as
 * a plain string list (not the library's `ActivityLogKind` union) so this
 * module stays library-free and independently testable. Any group whose kind
 * falls outside this set is treated as a non-renderable internal event.
 */
export const RENDERABLE_ACTIVITY_KINDS: readonly string[] = [
  'read',
  'search',
  'command',
  'edit',
  'task',
  'todo',
  'error',
  'message',
  'orchestrator',
  'supervisor',
  'other',
];

export function isRenderableActivityKind(kind: string): boolean {
  return RENDERABLE_ACTIVITY_KINDS.includes(kind);
}

/**
 * Markers the log pipeline appends when it cut a physical line: the remote
 * runner's `LogShipper` (64 KiB shipping cap) and the backend
 * `CliOutputLogParser` (64 KiB read cap, number formatted per host locale).
 */
const TRUNCATION_MARKER_PATTERNS: readonly RegExp[] = [
  /\s*\[runner: event payload truncated\]\s*$/,
  /\s*\u2026\[truncated: line exceeded [^\]]*\]\s*$/,
];

/** Codex JSONL frames start with the event type; only those are rebuilt. */
const CODEX_FRAME_START = /^\{\s*"type"\s*:\s*"(?:item|turn|thread|session)\./;

/** Note appended where the payload was cut so the reader sees why it ends abruptly. */
export const TRUNCATED_PAYLOAD_NOTE =
  '\u2026 [payload cut at the 64 KiB log line cap; the full output is only in the run log]';

/** Upper bound on retries when the cut sits between JSON tokens. */
const MAX_REPAIR_ATTEMPTS = 8;

/** True when the line carries a pipeline truncation marker and starts like JSON. */
export function isTruncatedJsonLine(text: string | undefined | null): boolean {
  if (!text) return false;
  const trimmed = text.trim();
  if (trimmed[0] !== '{' && trimmed[0] !== '[') return false;
  return TRUNCATION_MARKER_PATTERNS.some((pattern) => pattern.test(trimmed));
}

/**
 * Rebuild a Codex JSONL frame that the log pipeline cut at its line cap into
 * valid JSON. Returns the repaired frame text, or null when the line is not a
 * cut Codex frame or cannot be closed into parseable JSON.
 *
 * The cut usually lands inside a long string (`aggregated_output`); the string
 * is closed with {@link TRUNCATED_PAYLOAD_NOTE} and every open object / array
 * is closed. When the cut sits between tokens (after a comma or colon, inside
 * a bare literal), the text is trimmed back to the last complete member and
 * the note goes into the item's output text instead. Structure, ids and the
 * command text survive; only the tail of the payload is lost, which the note
 * says explicitly.
 */
export function repairTruncatedCodexFrame(text: string | undefined | null): string | null {
  if (!text) return null;
  const trimmed = text.trim();
  const marker = TRUNCATION_MARKER_PATTERNS.find((pattern) => pattern.test(trimmed));
  if (!marker || !CODEX_FRAME_START.test(trimmed)) return null;

  let body = trimmed.replace(marker, '');
  for (let attempt = 0; attempt < MAX_REPAIR_ATTEMPTS; attempt++) {
    const scan = scanJson(body);
    const closed = closeJson(body, scan);
    try {
      const frame = JSON.parse(closed) as unknown;
      if (!isPlainObject(frame)) return null;
      return JSON.stringify(annotateTruncatedFrame(frame, scan.inString));
    } catch {
      if (scan.lastComma < 0) return null;
      body = body.slice(0, scan.lastComma);
    }
  }
  return null;
}

interface JsonScan {
  /** The text ends inside a string literal. */
  inString: boolean;
  /** Start index of an escape sequence the cut left unfinished, or -1. */
  pendingEscapeStart: number;
  /** Open containers in nesting order. */
  stack: ('{' | '[')[];
  /** Index of the last `,` outside a string: the last safe point to trim to. */
  lastComma: number;
}

/** Single pass over a JSON prefix: string state, open containers, last comma. */
function scanJson(body: string): JsonScan {
  let inString = false;
  let escaping = false;
  let unicodeDigitsLeft = 0;
  let pendingEscapeStart = -1;
  let lastComma = -1;
  const stack: ('{' | '[')[] = [];

  for (let i = 0; i < body.length; i++) {
    const c = body[i];
    if (inString) {
      if (unicodeDigitsLeft > 0) {
        unicodeDigitsLeft--;
        if (unicodeDigitsLeft === 0) pendingEscapeStart = -1;
      } else if (escaping) {
        escaping = false;
        if (c === 'u') unicodeDigitsLeft = 4;
        else pendingEscapeStart = -1;
      } else if (c === '\\') {
        escaping = true;
        pendingEscapeStart = i;
      } else if (c === '"') {
        inString = false;
      }
      continue;
    }
    if (c === '"') inString = true;
    else if (c === '{' || c === '[') stack.push(c);
    else if (c === '}' || c === ']') stack.pop();
    else if (c === ',') lastComma = i;
  }

  return {
    inString,
    pendingEscapeStart: escaping || unicodeDigitsLeft > 0 ? pendingEscapeStart : -1,
    stack,
    lastComma,
  };
}

/** Close the open string (with the note) and every open container. */
function closeJson(body: string, scan: JsonScan): string {
  let head = body;
  if (scan.inString) {
    if (scan.pendingEscapeStart >= 0) head = head.slice(0, scan.pendingEscapeStart);
    // JSON.stringify yields a quoted string; drop its outer quotes to append.
    head += JSON.stringify(`\n${TRUNCATED_PAYLOAD_NOTE}`).slice(1, -1) + '"';
  }
  const closers = [...scan.stack].reverse().map((open) => (open === '{' ? '}' : ']')).join('');
  return head + closers;
}

/**
 * When the cut sat inside a string the note is already part of that string.
 * Otherwise put it on the item's output text so the tool card still shows it.
 */
function annotateTruncatedFrame(
  frame: Record<string, unknown>,
  cutInsideString: boolean,
): Record<string, unknown> {
  if (cutInsideString) return frame;
  const item = frame['item'];
  if (!isPlainObject(item)) return frame;
  let field: 'aggregated_output' | 'text' | null = null;
  if (typeof item['aggregated_output'] === 'string') field = 'aggregated_output';
  else if (typeof item['text'] === 'string') field = 'text';
  if (field === null) return frame;
  const existing = item[field] as string;
  const annotated = existing ? `${existing}\n${TRUNCATED_PAYLOAD_NOTE}` : TRUNCATED_PAYLOAD_NOTE;
  return { ...frame, item: { ...item, [field]: annotated } };
}

/**
 * True when the line text is a raw Anthropic `stream-json` transport frame that
 * must not be rendered as chat content. Returns false for ordinary prose, for
 * JSON snippets that are not transport frames, and for Codex JSONL frames the
 * library handles.
 */
export function isNonRenderableRawLine(text: string | undefined | null): boolean {
  if (!text) return false;
  if (parseProtocolNoveltyMarker(text.trim()) !== null) return true;
  const trimmed = text.trim();
  // A complete JSON value serialised onto one line. Partial pretty-printed
  // fragments (a lone `{`) are not complete JSON and are intentionally out of
  // scope - they cannot be reliably distinguished from prose in isolation.
  if (trimmed.length < 2) return false;
  const first = trimmed[0];
  if (first !== '{' && first !== '[') return false;
  const last = trimmed[trimmed.length - 1];
  if ((first === '{' && last !== '}') || (first === '[' && last !== ']')) return false;

  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    return false;
  }
  return isTransportFrame(parsed);
}

function isTransportFrame(value: unknown): boolean {
  if (Array.isArray(value)) {
    // A bare content-block array (e.g. a serialised `content: [...]`) is also
    // transport, when at least one element is itself a strong frame part.
    return value.some((element) => isStrongFramePart(element));
  }
  if (!isPlainObject(value)) return false;

  const type = typeof value['type'] === 'string' ? (value['type'] as string) : null;

  // Codex frames belong to the library - never redact them.
  if (type && CODEX_FRAME_TYPE_PREFIXES.some((prefix) => type.startsWith(prefix))) return false;
  if ('item' in value && type && type.startsWith('item')) return false;

  if (type && STRONG_STREAM_JSON_TYPES.has(type)) return true;

  const hasShapeKey = STREAM_JSON_SHAPE_KEYS.some((key) => key in value);

  // Weak/generic type, but shaped like a Messages frame.
  if (type && WEAK_STREAM_JSON_TYPES.has(type) && hasShapeKey) return true;

  // Unknown / brand-new event type that still carries transport structure.
  // This is the case the regression catalog pins: a shape we have never seen
  // must degrade to "[internal event]", never to raw JSON.
  if (type && hasShapeKey) return true;

  // A bare `{ role, content }` Messages envelope with no `type`.
  const role = typeof value['role'] === 'string' ? (value['role'] as string) : null;
  if (role && MESSAGE_ENVELOPE_ROLES.has(role) && 'content' in value) return true;

  return false;
}

/** A single content-block part with a strong transport `type` (`text`,
 * `tool_use`, `tool_result`, `thinking`, ...). Used for array top-levels. */
function isStrongFramePart(value: unknown): boolean {
  if (!isPlainObject(value)) return false;
  const type = typeof value['type'] === 'string' ? (value['type'] as string) : null;
  if (!type) return false;
  if (STRONG_STREAM_JSON_TYPES.has(type)) return true;
  // `text` blocks only count inside an array of parts (they are meaningless as
  // a standalone frame but decisive as a content-block element).
  return type === 'text' && ('text' in value || 'signature' in value);
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Sanitise a CLI-output line buffer for the conversation projection.
 *
 * Every run of consecutive non-renderable transport frames collapses into a
 * single `[internal event]` marker line (keeping the buffer compact when a CLI
 * dumps a burst of frames), with the raw JSON preserved on `internalDetail`.
 * Renderable lines pass through untouched. The input array is returned as-is
 * (same reference) when nothing needed redacting, so downstream memoisation and
 * change detection are not disturbed on the common path.
 */
/**
 * A pretty-printed transport frame (`JSON.stringify(frame, null, 2)`) opens
 * with a physical line that is the bare opener and nothing else. The log
 * pipeline splits stdout on `\n`, so each physical line of such a frame
 * arrives as its own {@link CliOutputLine} - none of them is complete JSON in
 * isolation, so {@link isNonRenderableRawLine} (which only ever looks at one
 * line) cannot see the frame and the raw fragments used to reach the chat as
 * a burst of tiny prose lines, or - once the library's own line-joining
 * kicked in - as one raw JSON blob (AGT-2793, "tool_result envelopes print as
 * text, repeated 3x"). The check is intentionally narrow (the trimmed line
 * must be exactly `{` or `[`, nothing else) so ordinary prose that merely
 * starts with a brace is never swept into the buffer.
 */
const MULTILINE_FRAME_OPENER = /^[{[]$/;

/** Bound on how many physical lines a buffered multi-line frame may span
 * before it is treated as a false positive and flushed unredacted. */
const MAX_MULTILINE_FRAME_LINES = 200;

interface PendingMultilineFrame {
  readonly first: CliOutputLine;
  readonly texts: string[];
}

export function sanitizeProjectionLines(
  lines: readonly CliOutputLine[]
): CliOutputLine[] {
  let anyRedacted = false;
  const out: CliOutputLine[] = [];
  // coding-agent-chat 0.3.2 recognises a Codex text-mode run as one stderr
  // transcript, then projects the following stdout as the agent's complete
  // answer. Preserve that stream boundary. Generic stderr prose outside this
  // envelope is still neutralised below so it cannot become one CLI failure
  // per physical line.
  let inCodexTextModeTranscript = false;
  // Non-null while the previous emitted line is an `[internal event]` marker,
  // so a run of consecutive frames folds into that one marker.
  let runDetails: string[] | null = null;
  // Non-null while accumulating the physical lines of a suspected
  // pretty-printed transport frame; see {@link MULTILINE_FRAME_OPENER}.
  let pendingFrame: PendingMultilineFrame | null = null;

  const emitNonRenderable = (timestamp: string, stream: string, detail: string): void => {
    anyRedacted = true;
    if (runDetails) {
      runDetails.push(detail);
      const marker = out[out.length - 1];
      out[out.length - 1] = {
        ...marker,
        text: protocolNoveltyGroupLabel(runDetails) ?? INTERNAL_EVENT_MARKER,
        internalDetail: joinDetails(runDetails),
      };
      return;
    }
    if (!detail.trim()) return;
    runDetails = [detail];
    out.push({
      timestamp,
      stream,
      text: protocolNoveltyGroupLabel(runDetails) ?? INTERNAL_EVENT_MARKER,
      internalDetail: detail,
    });
  };

  const emitLine = (line: CliOutputLine, cleanText: string, preserveCodexStderr: boolean): void => {
    const cleanLine = normalizeProjectionLine(line, cleanText, preserveCodexStderr);
    if (cleanLine !== line) anyRedacted = true;
    if (isNonRenderableRawLine(cleanText) || isTruncatedJsonLine(cleanText)) {
      emitNonRenderable(cleanLine.timestamp, cleanLine.stream, cleanText);
      return;
    }
    runDetails = null;
    out.push(cleanLine);
  };

  // Give up on the current buffer: replay every accumulated fragment through
  // the ordinary single-line path so nothing is lost when the suspected
  // frame turns out not to be one (or never closes before EOF).
  const flushPendingFrame = (): void => {
    if (!pendingFrame) return;
    const { first, texts } = pendingFrame;
    pendingFrame = null;
    emitLine(first, texts[0], inCodexTextModeTranscript);
    for (const text of texts.slice(1)) {
      emitLine({ timestamp: first.timestamp, stream: first.stream, text }, text, inCodexTextModeTranscript);
    }
  };

  for (const line of lines) {
    const rawText = stripAnsi(line.text);
    // A frame the pipeline cut at its line cap is no longer valid JSON. Rebuild
    // it so the library projects a tool call instead of raw agent prose.
    const repaired = repairTruncatedCodexFrame(rawText);
    const cleanText = repaired ?? rawText;
    if (repaired !== null) anyRedacted = true;
    // Codex todo_list snapshots have a dedicated living checklist fed from
    // the typed PlanUpdated path. Keep the original line in the untouched
    // Trace input, but do not hand it to CAC where every item.updated frame
    // would otherwise become another row in the readable conversation.
    if (isCodexTodoListFrame(cleanText)) {
      anyRedacted = true;
      runDetails = null;
      continue;
    }
    if (
      (line.stream === 'system' && CODEX_EXEC_RUNNER_MARKER.test(cleanText))
      || (line.stream === 'stderr' && CODEX_TEXT_MODE_BANNER.test(cleanText))
    ) {
      inCodexTextModeTranscript = true;
    } else if (
      inCodexTextModeTranscript
      && (
        SUPERVISOR_ENVELOPE.test(cleanText)
        || (line.stream !== 'stderr' && cleanText.trim() !== '')
      )
    ) {
      inCodexTextModeTranscript = false;
    }

    if (pendingFrame) {
      pendingFrame.texts.push(cleanText);
      const joined = pendingFrame.texts.join('\n');
      const scan = scanJson(joined);
      if (scan.stack.length === 0 && !scan.inString && scan.pendingEscapeStart < 0) {
        // Braces balanced: the buffer is either a complete transport frame or
        // a false positive that merely closed its brace count by chance.
        let parsed: unknown;
        try {
          parsed = JSON.parse(joined);
        } catch {
          parsed = undefined;
        }
        if (parsed !== undefined && isTransportFrame(parsed)) {
          const first = pendingFrame.first;
          pendingFrame = null;
          emitNonRenderable(first.timestamp, first.stream, joined);
        } else {
          flushPendingFrame();
        }
      } else if (pendingFrame.texts.length >= MAX_MULTILINE_FRAME_LINES) {
        flushPendingFrame();
      }
      continue;
    }

    // A truncated frame is still handled on the single-line path below (it is
    // never a bare `{`/`[` opener followed by more lines - the pipeline cuts
    // it mid-payload on the SAME physical line). Only a genuine pretty-printed
    // frame opener starts multi-line buffering.
    if (
      !inCodexTextModeTranscript
      && repaired === null
      && MULTILINE_FRAME_OPENER.test(cleanText.trim())
    ) {
      pendingFrame = { first: line, texts: [cleanText] };
      continue;
    }

    // emitLine's isTruncatedJsonLine(cleanText) check subsumes the original
    // `repaired === null && isTruncatedJsonLine(rawText)` guard: cleanText
    // equals rawText whenever repaired is null.
    emitLine(line, cleanText, inCodexTextModeTranscript);
  }

  flushPendingFrame();

  return anyRedacted ? out : (lines as CliOutputLine[]);
}

export function isCodexTodoListFrame(text: string | undefined | null): boolean {
  if (!text || !text.includes('todo_list')) return false;
  try {
    const frame = JSON.parse(text) as Record<string, unknown>;
    if (!['item.started', 'item.updated', 'item.completed'].includes(String(frame['type']))) return false;
    const item = frame['item'];
    return isPlainObject(item) && item['type'] === 'todo_list' && Array.isArray(item['items']);
  } catch {
    return false;
  }
}

function normalizeProjectionLine(
  line: CliOutputLine,
  cleanText: string,
  preserveCodexStderr: boolean,
): CliOutputLine {
  const supervisor = SUPERVISOR_ENVELOPE.exec(cleanText);
  if (supervisor?.groups) {
    const action = titleCaseAction(supervisor.groups['action']);
    const body = supervisor.groups['body'].trim();
    return {
      ...line,
      stream: 'supervisor',
      text: body ? `**${action}** · ${body}` : `**${action}**`,
    };
  }

  if (line.stream === 'stderr' && !preserveCodexStderr && !isGenuineCliError(cleanText)) {
    return { ...line, stream: 'stdout', text: cleanText };
  }

  return cleanText === line.text ? line : { ...line, text: cleanText };
}

function isGenuineCliError(text: string): boolean {
  return GENUINE_CLI_ERROR_PATTERNS.some((pattern) => pattern.test(text));
}

function titleCaseAction(action: string): string {
  return action
    .trim()
    .replace(/[-_]+/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

/** Cap the disclosed raw detail so a pathological frame burst cannot bloat the
 * DOM; the marker still signals that internal events occurred. */
const MAX_DETAIL_CHARS = 20_000;

function joinDetails(details: readonly string[]): string {
  const joined = details.join('\n');
  if (joined.length <= MAX_DETAIL_CHARS) return joined;
  return `${joined.slice(0, MAX_DETAIL_CHARS)}\n… (${details.length} frames, truncated)`;
}

/**
 * True when the line is a redacted internal-event marker produced above: the
 * generic `[internal event]` placeholder, or the friendlier protocol-novelty
 * group summary ({@link protocolNoveltyGroupLabel}) - both carry the original
 * raw payload on `internalDetail` for on-demand disclosure.
 */
export function isInternalEventLine(line: CliOutputLine): boolean {
  return typeof line.internalDetail === 'string' && line.internalDetail.trim().length > 0;
}

/**
 * When every frame collapsed into one run is the same {@link
 * ProtocolNoveltyMarker} identity (cli, adapter, frame type), replace the
 * generic `[internal event]` marker with a one-line, muted summary carrying
 * the occurrence count - a diagnostic collapses into one row per kind, not one
 * raw JSON block per occurrence. Mixed or non-novelty runs keep the generic
 * marker; their payloads still disclose in full via `internalDetail`.
 */
function protocolNoveltyGroupLabel(details: readonly string[]): string | null {
  const markers = details.map((detail) => parseProtocolNoveltyMarker(detail));
  const first = markers[0];
  if (!first) return null;
  const homogeneous = markers.every((marker) =>
    marker !== null
    && marker.cli === first.cli
    && marker.adapterVersion === first.adapterVersion
    && marker.frameType === first.frameType);
  if (!homogeneous) return null;
  const count = markers.length;
  return `${count} unknown ${count === 1 ? 'frame' : 'frames'} of type ${first.frameType}`
    + ` (${first.cli} adapter ${first.adapterVersion})`;
}
