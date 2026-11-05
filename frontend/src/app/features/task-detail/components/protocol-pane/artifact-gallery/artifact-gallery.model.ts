import type {
  ArtifactImageEvent,
  ConversationEvent,
  MessageEvent,
  ToolBurstEvent,
} from 'coding-agent-chat/core';
import { resolveTaskArtifactLink } from '../../task-artifact-links/task-artifact-link';
import { resolveProtocolImageSrc } from '../protocol-image-resolver';

export type ConversationArtifactKind =
  | 'image'
  | 'diff'
  | 'markdown'
  | 'html'
  | 'json'
  | 'log';

export interface ConversationArtifact {
  readonly id: string;
  readonly kind: ConversationArtifactKind;
  readonly path: string;
  readonly fileName: string;
  readonly label: string;
  readonly url: string;
  readonly contentUrl: string | null;
}

export type PresentedArtifactEvent = ArtifactImageEvent & {
  readonly artifactPresentation: ConversationArtifact;
  readonly artifactGroupId: string;
};

export interface ConversationArtifactBlock {
  readonly id: string;
  /** Ordinal among the library's rendered artifact-image rows. */
  readonly startOrdinal: number;
  readonly rowCount: number;
  readonly artifacts: readonly ConversationArtifact[];
}

interface ParsedArtifactLine {
  readonly path: string;
  readonly label: string;
  readonly kind: ConversationArtifactKind;
}

const MESSAGE_KINDS = new Set<ConversationEvent['kind']>([
  'message.user',
  'message.taskAgent',
  'message.orchestrator',
  'message.supervisor',
  'message.supportingAgent',
]);

const EXTENSIONS: Readonly<Record<string, ConversationArtifactKind>> = {
  png: 'image',
  jpg: 'image',
  jpeg: 'image',
  webp: 'image',
  gif: 'image',
  diff: 'diff',
  patch: 'diff',
  md: 'markdown',
  markdown: 'markdown',
  html: 'html',
  htm: 'html',
  json: 'json',
  log: 'log',
};

/** Add task-scoped artifact presentation after the core conversation projection. */
export function presentArtifactEvents(
  events: readonly ConversationEvent[],
  jobId: string,
  watchPath: string | null | undefined,
): ConversationEvent[] {
  const presented: ConversationEvent[] = [];
  for (const event of omitMessageReferencedScreenshots(events)) {
    if (MESSAGE_KINDS.has(event.kind)) {
      presented.push(...promoteMessageArtifactLines(event, jobId, watchPath));
      continue;
    }
    if (event.kind === 'toolBurst') {
      const recognized = (event.artifacts ?? []).filter((path) => classifyArtifactPath(path));
      const other = (event.artifacts ?? []).filter((path) => !classifyArtifactPath(path));
      presented.push({ ...event, artifacts: other.length > 0 ? other : undefined });
      presented.push(...recognized.map((path, index) =>
        artifactEvent(event, path, index, jobId, watchPath)));
      continue;
    }
    presented.push(event);
  }
  return groupPresentedArtifacts(presented, jobId, watchPath);
}

/**
 * Promote contiguous artifact-only lines out of agent prose. The shared chat
 * library intentionally stays task-folder agnostic; this host projection binds
 * the paths to the open task and leaves all ordinary Markdown untouched.
 *
 * A single image remains in the original Markdown message so its established
 * inline rendering does not change. Multi-image and mixed/document runs become
 * typed artifact events consumed by the compatibility directive.
 */
export function promoteMessageArtifactLines(
  event: ConversationEvent,
  jobId: string,
  watchPath: string | null | undefined,
): ConversationEvent[] {
  if (!MESSAGE_KINDS.has(event.kind)) return [event];
  const message = event as MessageEvent;
  const lines = message.body.split(/\r?\n/);
  if (lines.length === 0) return [event];

  const output: ConversationEvent[] = [];
  const prose: string[] = [];
  let segment = 0;
  let index = 0;

  const flushProse = (): void => {
    const body = trimBlankEdges(prose).join('\n');
    prose.length = 0;
    if (!body) return;
    output.push({ ...message, id: `${message.id}:text:${segment++}`, body });
  };

  while (index < lines.length) {
    const first = parseArtifactLine(lines[index]);
    if (!first) {
      prose.push(lines[index]);
      index += 1;
      continue;
    }

    const run: ParsedArtifactLine[] = [first];
    const original: string[] = [lines[index]];
    index += 1;
    while (index < lines.length) {
      const next = parseArtifactLine(lines[index]);
      if (!next) break;
      run.push(next);
      original.push(lines[index]);
      index += 1;
    }

    // Preserve the established inline case exactly as-is.
    if (run.length === 1 && run[0].kind === 'image') {
      prose.push(...original);
      continue;
    }

    flushProse();
    const groupId = `${message.id}:artifacts:${segment++}`;
    run.forEach((artifact, artifactIndex) => {
      output.push(toPresentedArtifactEvent(
        message,
        artifact,
        `${groupId}:${artifactIndex}`,
        groupId,
        jobId,
        watchPath,
      ));
    });
  }

  flushProse();
  return output.length > 0 ? output : [event];
}

/**
 * Mark every contiguous multi-image run, plus every typed document run, as one
 * gallery block. Unknown extensions never become artifact events and therefore
 * retain the library's existing line treatment.
 */
export function groupPresentedArtifacts(
  events: readonly ConversationEvent[],
  jobId: string,
  watchPath: string | null | undefined,
): ConversationEvent[] {
  const output = [...events];
  let index = 0;
  while (index < output.length) {
    if (output[index].kind !== 'artifact.image') {
      index += 1;
      continue;
    }
    const start = index;
    const first = output[start] as ArtifactImageEvent;
    while (index < output.length && belongsToArtifactRun(first, output[index])) index += 1;
    const length = index - start;
    const run = output.slice(start, index) as ArtifactImageEvent[];
    const hasDocument = run.some((candidate) =>
      isPresentedArtifactEvent(candidate)
      && candidate.artifactPresentation.kind !== 'image');
    if (length === 1 && !hasDocument) continue;

    const groupId = run.find(isPresentedArtifactEvent)?.artifactGroupId
      ?? `artifact-group:${run[0].id}`;
    run.forEach((candidate, runIndex) => {
      const artifact = isPresentedArtifactEvent(candidate)
        ? candidate.artifactPresentation
        : artifactFromImageEvent(candidate, jobId, watchPath);
      output[start + runIndex] = {
        ...candidate,
        // The library row is hidden by the host directive, but the browser can
        // begin fetching it before that DOM pass. Bind only the original URL here;
        // the gallery metadata retains the full source for the lightbox.
        url: artifact.kind === 'image' ? artifact.url : null,
        artifactPresentation: artifact,
        artifactGroupId: groupId,
      } as PresentedArtifactEvent;
    });
  }
  return output;
}

/**
 * The task screenshot catalogue is projected after message events by the
 * shared chat library. When an agent message already links those same result
 * images, retaining both copies would split one mixed artifact block and show
 * every image twice. Keep the message-owned representation and omit only the
 * duplicate catalogue events; unrelated screenshots remain untouched.
 */
export function omitMessageReferencedScreenshots(
  events: readonly ConversationEvent[],
): ConversationEvent[] {
  const referenced = new Set<string>();
  for (const event of events) {
    if (!MESSAGE_KINDS.has(event.kind)) continue;
    for (const line of (event as MessageEvent).body.split(/\r?\n/)) {
      const parsed = parseArtifactLine(line);
      if (parsed?.kind === 'image') referenced.add(pathIdentity(parsed.path));
    }
  }
  if (referenced.size === 0) return [...events];

  return events.filter((event) => {
    if (event.kind !== 'artifact.image' || event.sourceTool !== 'screenshot') return true;
    const path = event.durablePath || event.sourcePath || '';
    return !referenced.has(pathIdentity(path));
  });
}

export function artifactBlocks(events: readonly ConversationEvent[]): ConversationArtifactBlock[] {
  const blocks: ConversationArtifactBlock[] = [];
  let artifactOrdinal = 0;
  let index = 0;

  while (index < events.length) {
    const event = events[index];
    if (event.kind !== 'artifact.image') {
      index += 1;
      continue;
    }

    const startOrdinal = artifactOrdinal;
    const run: ArtifactImageEvent[] = [];
    while (index < events.length && belongsToArtifactRun(event as ArtifactImageEvent, events[index])) {
      run.push(events[index] as ArtifactImageEvent);
      artifactOrdinal += 1;
      index += 1;
    }

    const presented = run.filter(isPresentedArtifactEvent);
    if (presented.length !== run.length || presented.length === 0) continue;
    const artifacts = presented.map((candidate) => candidate.artifactPresentation);
    if (artifacts.length === 1 && artifacts[0].kind === 'image') continue;
    blocks.push({
      id: presented[0].artifactGroupId,
      startOrdinal,
      rowCount: run.length,
      artifacts,
    });
  }

  return blocks;
}

export function isPresentedArtifactEvent(
  event: ConversationEvent,
): event is PresentedArtifactEvent {
  return event.kind === 'artifact.image'
    && 'artifactPresentation' in event
    && 'artifactGroupId' in event;
}

function belongsToArtifactRun(
  first: ArtifactImageEvent,
  candidate: ConversationEvent,
): candidate is ArtifactImageEvent {
  if (candidate.kind !== 'artifact.image') return false;
  const firstPresented = isPresentedArtifactEvent(first);
  const candidatePresented = isPresentedArtifactEvent(candidate);
  if (!firstPresented) return !candidatePresented;
  return candidatePresented && candidate.artifactGroupId === first.artifactGroupId;
}

export function classifyArtifactPath(path: string): ConversationArtifactKind | null {
  const clean = path.split(/[?#]/, 1)[0];
  const extension = clean.includes('.') ? clean.slice(clean.lastIndexOf('.') + 1).toLowerCase() : '';
  return EXTENSIONS[extension] ?? null;
}

export function artifactFromPath(
  id: string,
  path: string,
  label: string,
  kind: ConversationArtifactKind,
  jobId: string,
  watchPath: string | null | undefined,
): ConversationArtifact {
  const normalized = normalizeArtifactPath(path);
  const fileName = decodedFileName(normalized);
  const url = kind === 'image'
    ? resolveProtocolImageSrc(normalized, jobId, watchPath)
    : resolveTaskArtifactLink(normalized, { jobId, watchPath })?.href ?? '';
  return {
    id,
    kind,
    path: normalized,
    fileName,
    label: label.trim() || fileName,
    url,
    contentUrl: kind === 'image' || kind === 'html'
      ? null
      : artifactContentUrl(normalized, jobId, watchPath),
  };
}

function toPresentedArtifactEvent(
  message: MessageEvent,
  parsed: ParsedArtifactLine,
  id: string,
  groupId: string,
  jobId: string,
  watchPath: string | null | undefined,
): PresentedArtifactEvent {
  const artifact = artifactFromPath(id, parsed.path, parsed.label, parsed.kind, jobId, watchPath);
  return {
    id,
    kind: 'artifact.image',
    timestamp: message.timestamp,
    runId: message.runId,
    model: message.model,
    thinkingLevel: message.thinkingLevel,
    rawRange: message.rawRange,
    caption: artifact.label,
    sourcePath: artifact.path,
    durablePath: artifact.path.startsWith('results/') ? artifact.path : null,
    sourceTool: 'agent-message',
    url: parsed.kind === 'image' ? artifact.url : null,
    artifactPresentation: artifact,
    artifactGroupId: groupId,
  };
}

function artifactEvent(
  burst: ToolBurstEvent,
  path: string,
  index: number,
  jobId: string,
  watchPath: string | null | undefined,
): PresentedArtifactEvent {
  const normalized = path.replace(/\\/g, '/');
  const fileName = normalized.split('/').pop() || normalized;
  const folder = normalized.slice(0, Math.max(0, normalized.lastIndexOf('/'))) || 'results';
  const kind = classifyArtifactPath(normalized) ?? 'image';
  const id = `${burst.id}:artifact:${index}`;
  const presentation = artifactFromPath(id, normalized, fileName, kind, jobId, watchPath);
  return {
    id,
    kind: 'artifact.image',
    timestamp: burst.timestamp,
    runId: burst.runId,
    model: burst.model,
    thinkingLevel: burst.thinkingLevel,
    rawRange: burst.rawRange,
    caption: `${folder} / ${fileName}`,
    sourcePath: normalized,
    durablePath: normalized.startsWith('results/') ? normalized : null,
    sourceTool: 'agent',
    url: kind === 'image' ? resolveProtocolImageSrc(normalized, jobId, watchPath) : null,
    artifactPresentation: presentation,
    artifactGroupId: `${burst.id}:artifacts`,
  };
}

function artifactFromImageEvent(
  event: ArtifactImageEvent,
  jobId: string,
  watchPath: string | null | undefined,
): ConversationArtifact {
  const path = event.durablePath || event.sourcePath || event.url || event.caption;
  const artifact = artifactFromPath(event.id, path, event.caption, 'image', jobId, watchPath);
  return {
    ...artifact,
    url: event.url || artifact.url,
  };
}

function parseArtifactLine(line: string): ParsedArtifactLine | null {
  const candidate = line.trim().replace(/^(?:[-*+]\s+|\d+[.)]\s+)/, '').trim();
  let label = '';
  let path: string;

  const markdown = /^!?\[([^\]]*)\]\(\s*(?:<([^>]+)>|([^\s)]+))(?:\s+["'][^"']*["'])?\s*\)(?:\s+\([^)]*\))?$/.exec(candidate);
  if (markdown) {
    label = markdown[1] ?? '';
    path = markdown[2] ?? markdown[3] ?? '';
  } else {
    const code = /^`([^`]+)`(?:\s+\([^)]*\))?$/.exec(candidate);
    if (code) path = code[1];
    else path = candidate;
  }

  path = safeDecode(path).replace(/\\/g, '/').replace(/^\.\//, '');
  if (!/^results\//i.test(path) || path.includes('..')) return null;
  const segments = path.split('/');
  if (segments.some((segment) => !segment || segment === '.' || /[\\/]/.test(safeDecode(segment)))) return null;
  const kind = classifyArtifactPath(path);
  if (!kind) return null;
  return { path: normalizeArtifactPath(path), label, kind };
}

function normalizeArtifactPath(path: string): string {
  return path.trim().split(/[?#]/, 1)[0]
    .replace(/\\/g, '/')
    .replace(/^\.\//, '')
    .replace(/^results\//i, 'results/');
}

function decodedFileName(path: string): string {
  return safeDecode(path.split('/').at(-1) ?? path);
}

function artifactContentUrl(
  path: string,
  jobId: string,
  watchPath: string | null | undefined,
): string {
  const encodedPath = path.split('/').map(encodeURIComponent).join('/');
  const query = [
    watchPath ? `watchPath=${encodeURIComponent(watchPath)}` : '',
    'scope=workspace',
  ].filter(Boolean).join('&');
  return `/api/tasks/${encodeURIComponent(jobId)}/files/${encodedPath}?${query}`;
}

function trimBlankEdges(lines: readonly string[]): string[] {
  let start = 0;
  let end = lines.length;
  while (start < end && !lines[start].trim()) start += 1;
  while (end > start && !lines[end - 1].trim()) end -= 1;
  return lines.slice(start, end);
}

function safeDecode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

function pathIdentity(path: string): string {
  return normalizeArtifactPath(safeDecode(path)).toLowerCase();
}
