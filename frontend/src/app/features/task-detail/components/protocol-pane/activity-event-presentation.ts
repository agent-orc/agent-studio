import type {
  ConversationEvent,
  ConversationEventSeverity,
  RawLineRange,
  SystemParserWarningEvent,
  SystemStatusEvent,
  ToolBurstEvent,
  ToolCommandExecution,
  ToolFamily,
} from 'coding-agent-chat/core';
import type { CliOutputLine } from '../../../../models/task.model';

const TOKEN_TOTAL = /\bTurn completed\s*\(tokens:\s*([\d,_]+)\)/i;
const FREE_COMPLETION_LINE = /^\s*(?:Session|Turn) completed(?:\s*\(tokens:\s*[\d,_]+\))?[.!]?\s*$/i;
const REPOSITORY_SEGMENT = /(?:^|\/)(\.agents|backend(?:\.Tests)?|docs|frontend|prompts|runner|scripts)(\/.*)?$/i;

const TOOL_FAMILY_ORDER: readonly ToolFamily[] = [
  'command', 'read', 'search', 'edit', 'task', 'todo', 'other',
];

export interface PresentedToolFile {
  /** Repository-relative path used by the compact and expanded UI. */
  displayPath: string;
  /** Original absolute path retained for hover disclosure. */
  fullPath: string;
}

export interface ToolBurstRowPresentation {
  /** Selects the compact row vocabulary without inferring from file presence. */
  kind: 'tool' | 'edit';
  /** Explicit operation and file-count semantics for the compact row. */
  primaryLabel: string;
  /** Top two tool families, already formatted for the compact row. */
  mixLabel: string;
  /** Aggregate result; failed events retain the library's error treatment. */
  outcomeLabel: string;
  /** Compact repository-relative file summary for edit-only rows. */
  pathLabel?: string;
  /** Newline-separated absolute paths for native hover disclosure. */
  fileTooltip?: string;
}

/**
 * Host-only extension consumed by the 0.3.x compatibility directive. The
 * library still receives a normal ToolBurstEvent and keeps its expandable
 * details; the extra metadata enriches only the compact Studio row.
 */
export type PresentedToolBurstEvent = ToolBurstEvent & {
  fileDetails?: readonly PresentedToolFile[];
  rowPresentation: ToolBurstRowPresentation;
};

export interface ActivityEventPresentationOptions {
  typedTurnCompletions?: boolean;
  /** Worktree cwd captured for a specific run index. */
  worktreeRootsByRun?: ReadonlyMap<number, string>;
  /** Used for unscoped legacy tool events. */
  fallbackWorktreeRoot?: string | null;
}

/** Remove transport-era completion prose when the typed lifecycle is authoritative. */
export function stripLegacyCompletionLines(
  lines: readonly CliOutputLine[],
  typedLifecycle: boolean,
): CliOutputLine[] {
  if (!typedLifecycle) return [...lines];
  return lines.filter(line => !FREE_COMPLETION_LINE.test(line.text));
}

/**
 * Product-specific cleanup between the shared conversation projector and the
 * embedded Activity view. It keeps parser diagnostics attached to the tool
 * operation they describe; task-scoped artifacts join at the lazy host seam.
 */
export function presentActivityEvents(
  events: readonly ConversationEvent[],
  jobId: string,
  watchPath: string | null | undefined,
  options: ActivityEventPresentationOptions = {},
): ConversationEvent[] {
  const presented: ConversationEvent[] = [];

  for (const event of events) {
    // projectConversation appends the open task itself as a final marker.
    // In a task-local Activity feed that repeats the surrounding card title
    // without representing a transition, which made it look like an
    // unexplained lane event. Real run and lane evidence remains untouched.
    if (event.kind === 'taskMarker') {
      continue;
    }

    if (event.kind === 'system.parserWarning') {
      const burstIndex = findOwningBurst(presented, event);
      if (burstIndex >= 0) {
        presented[burstIndex] = attachParserDetail(presented[burstIndex] as ToolBurstEvent, event);
        continue;
      }
    }

    if (event.kind === 'toolBurst') {
      const capturedWorktreeRoot = event.runId == null
        ? options.fallbackWorktreeRoot
        : options.worktreeRootsByRun?.get(event.runId) ?? options.fallbackWorktreeRoot;
      const toolBurst = presentToolBurst(
        event,
        capturedWorktreeRoot ?? watchPath,
        jobId,
      );
      presented.push(toolBurst);
      continue;
    }

    if (
      event.kind === 'decision.orchestrator'
      && event.decisionType.toLowerCase() === 'reissue'
      && event.action?.toLowerCase() === 'reissue'
    ) {
      presented.push({ ...event, action: undefined });
      continue;
    }

    if (event.kind === 'system.status' && TOKEN_TOTAL.test(`${event.label} ${event.explanation}`)) {
      if (!options.typedTurnCompletions) presented.push(...formatCompletion(event));
      continue;
    }

    presented.push(event);
  }

  return groupConsecutiveRunnerStatus(presented);
}

/**
 * One fact from a collapsed runner status block, kept for the per-row
 * disclosure the directive renders nested under the single group affordance.
 */
export interface RunnerGroupRow {
  id: string;
  label: string;
  explanation: string;
  severity: ConversationEventSeverity;
  rawRange: RawLineRange;
}

/**
 * Host-only extension of a `system.status` event that stands in for a run of
 * consecutive runner-category rows. The library still renders one ordinary
 * `<li>` for it (category `runner-group`, label "Runner"); the compatibility
 * directive reads {@link runnerGroupRows} to nest the per-fact breakdown
 * under a single disclosure instead of letting the library repeat one `<li>`
 * per fact with its own "trace" button (AGT-2793, "Runner rows block").
 */
export type PresentedRunnerGroupEvent = SystemStatusEvent & {
  category: 'runner-group';
  runnerGroupRows: readonly RunnerGroupRow[];
};

export function isPresentedRunnerGroupEvent(
  event: ConversationEvent,
): event is PresentedRunnerGroupEvent {
  return event.kind === 'system.status' && event.category === 'runner-group' && 'runnerGroupRows' in event;
}

const RUNNER_SEVERITY_RANK: Record<ConversationEventSeverity, number> = {
  error: 3,
  warn: 2,
  info: 1,
};

/**
 * Collapse every run of consecutive `category: 'runner'` status events into
 * one `runner-group` event. A single runner fact between unrelated events
 * stays as-is (nothing to group); grouping a lone fact would only add a
 * disclosure around one line for no benefit.
 */
function groupConsecutiveRunnerStatus(events: readonly ConversationEvent[]): ConversationEvent[] {
  const out: ConversationEvent[] = [];
  let run: SystemStatusEvent[] = [];

  const flush = () => {
    if (run.length === 0) return;
    out.push(run.length === 1 ? run[0] : mergeRunnerGroup(run));
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

function mergeRunnerGroup(events: readonly SystemStatusEvent[]): PresentedRunnerGroupEvent {
  const first = events[0];
  const last = events[events.length - 1];
  const worstSeverity = events.reduce<ConversationEventSeverity>((worst, event) => {
    const severity = event.severity ?? 'info';
    return RUNNER_SEVERITY_RANK[severity] > RUNNER_SEVERITY_RANK[worst] ? severity : worst;
  }, 'info');
  return {
    ...first,
    id: `${first.id}:group`,
    category: 'runner-group',
    label: 'Runner',
    explanation: `${events.length} runner updates`,
    severity: worstSeverity,
    rawRange: { source: first.rawRange.source, start: first.rawRange.start, end: last.rawRange.end },
    runnerGroupRows: events.map((event) => ({
      id: event.id,
      label: event.label,
      explanation: event.explanation,
      severity: event.severity ?? 'info',
      rawRange: event.rawRange,
    })),
  };
}

export function isPresentedToolBurstEvent(
  event: ConversationEvent,
): event is PresentedToolBurstEvent {
  return event.kind === 'toolBurst' && 'rowPresentation' in event;
}

function presentToolBurst(
  event: ToolBurstEvent,
  capturedWorktreeRoot: string | null | undefined,
  jobId: string,
): PresentedToolBurstEvent {
  // A captured CLI cwd can sit below the linked-worktree root. Recover the
  // root from the task segment or the two canonical worktree-pool layouts.
  const worktreeRoot = capturedWorktreeRoot
    ? canonicalWorktreeRoot(capturedWorktreeRoot, jobId)
    : null;
  const editOnly = event.count > 0 && event.families['edit'] === event.count;
  const rawFiles = unique([
    ...(event.files ?? []),
    ...(editOnly ? editPathsFromSample(event.samples?.['edit']) : []),
  ]);
  const fileDetails = uniquePresentedFiles(
    rawFiles.map((path) => presentToolFile(path, worktreeRoot)),
  );
  const samples = event.samples?.['edit'] && worktreeRoot
    ? { ...event.samples, edit: stripWorktreeRoot(event.samples['edit'], worktreeRoot) }
    : event.samples;
  const files = fileDetails?.map((file) => file.displayPath);

  return {
    ...event,
    files,
    fileDetails,
    samples,
    rowPresentation: toolBurstRowPresentation(event, editOnly, fileDetails),
  };
}

function toolBurstRowPresentation(
  event: ToolBurstEvent,
  editOnly: boolean,
  fileDetails: readonly PresentedToolFile[] | undefined,
): ToolBurstRowPresentation {
  const operationLabel = editOnly
    ? `${event.count} ${event.count === 1 ? 'Edit' : 'Edits'}`
    : `${event.count} Tool ${event.count === 1 ? 'call' : 'calls'}`;
  const fileCount = fileDetails?.length ?? 0;
  const fileLabel = editOnly
    ? ` · ${fileCount} ${fileCount === 1 ? 'file' : 'files'}`
    : '';
  const mixLabel = editOnly ? '' : topToolFamilies(event)
    .map(([family, count]) => `${toolFamilyLabel(family)} ×${count}`)
    .join(', ');
  const pathLabel = editOnly && fileDetails?.length
    ? fileDetails.length === 1
      ? fileDetails[0].displayPath
      : `${fileDetails[0].displayPath} +${fileDetails.length - 1} more`
    : undefined;

  return {
    kind: editOnly ? 'edit' : 'tool',
    primaryLabel: `${operationLabel}${fileLabel}`,
    mixLabel,
    outcomeLabel: event.failures > 0 ? `${event.failures} failed` : 'all ok',
    pathLabel,
    fileTooltip: fileDetails?.length
      ? fileDetails.map((file) => file.fullPath).join('\n')
      : undefined,
  };
}

function topToolFamilies(event: ToolBurstEvent): [ToolFamily, number][] {
  return TOOL_FAMILY_ORDER
    .map((family): [ToolFamily, number] => [family, event.families[family] ?? 0])
    .filter(([, count]) => Number.isFinite(count) && count > 0)
    .sort((left, right) => right[1] - left[1]
      || TOOL_FAMILY_ORDER.indexOf(left[0]) - TOOL_FAMILY_ORDER.indexOf(right[0]))
    .slice(0, 2);
}

function toolFamilyLabel(family: ToolFamily): string {
  return family === 'command' ? 'shell' : family === 'other' ? 'tool' : family;
}

function editPathsFromSample(sample: string | undefined): string[] {
  if (!sample) return [];
  const paths: string[] = [];
  const pattern = /(?:^|,\s*)Edit\s+(.+?)(?=,\s*Edit\s+|$)/gi;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(sample)) !== null) {
    const path = match[1].trim();
    if (path) paths.push(path);
  }
  return paths;
}

function presentToolFile(path: string, worktreeRoot: string | null): PresentedToolFile {
  const normalized = normalizePath(path);
  const fullPath = worktreeRoot && !isAbsoluteOrResourcePath(normalized)
    ? `${worktreeRoot.replace(/\/+$/, '')}/${normalized.replace(/^\.\//, '')}`
    : normalized;
  return {
    displayPath: repoRelativePath(fullPath, worktreeRoot),
    fullPath,
  };
}

function uniquePresentedFiles(
  files: readonly PresentedToolFile[],
): PresentedToolFile[] | undefined {
  if (files.length === 0) return undefined;
  const seen = new Set<string>();
  return files.filter((file) => {
    const key = /^[a-z]:\//i.test(file.fullPath) ? file.fullPath.toLowerCase() : file.fullPath;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function canonicalWorktreeRoot(path: string, jobId: string): string {
  const normalized = normalizePath(path).replace(/\/+$/, '');
  const segments = normalized.split('/');
  const taskSegment = segments.findIndex((segment) => segment.toLowerCase() === jobId.toLowerCase());
  if (taskSegment >= 0 && segments.slice(0, taskSegment).some((segment) =>
    /^(?:ass-)?worktrees$/i.test(segment))) {
    return segments.slice(0, taskSegment + 1).join('/');
  }
  const assPool = /^(.*?\/ass-worktrees\/[^/]+\/[^/]+)(?:\/|$)/i.exec(normalized);
  if (assPool) return assPool[1];
  const runnerPool = /^(.*?\/worktrees\/[^/]+)(?:\/|$)/i.exec(normalized);
  return runnerPool?.[1] ?? normalized;
}

function repoRelativePath(path: string, worktreeRoot: string | null): string {
  if (!isAbsoluteOrResourcePath(path)) return path.replace(/^\.\//, '');
  if (worktreeRoot) {
    const stripped = stripExactRoot(path, worktreeRoot);
    if (stripped !== null) return stripped;
  }
  const repositoryMatch = REPOSITORY_SEGMENT.exec(path);
  return repositoryMatch
    ? `${repositoryMatch[1]}${repositoryMatch[2] ?? ''}`
    : path;
}

function stripExactRoot(path: string, root: string): string | null {
  const normalizedRoot = normalizePath(root).replace(/\/+$/, '');
  const caseInsensitive = /^[a-z]:\//i.test(normalizedRoot);
  const comparablePath = caseInsensitive ? path.toLowerCase() : path;
  const comparableRoot = caseInsensitive ? normalizedRoot.toLowerCase() : normalizedRoot;
  if (!comparablePath.startsWith(`${comparableRoot}/`)) return null;
  return path.slice(normalizedRoot.length + 1);
}

function stripWorktreeRoot(value: string, worktreeRoot: string): string {
  const normalized = normalizePath(value);
  const root = normalizePath(worktreeRoot).replace(/\/+$/, '');
  const caseInsensitive = /^[a-z]:\//i.test(root);
  const comparableValue = caseInsensitive ? normalized.toLowerCase() : normalized;
  const comparableRoot = caseInsensitive ? root.toLowerCase() : root;
  const rootIndex = comparableValue.indexOf(`${comparableRoot}/`);
  if (rootIndex < 0) return normalized;
  return normalized.slice(0, rootIndex) + normalized.slice(rootIndex + root.length + 1);
}

function isAbsoluteOrResourcePath(path: string): boolean {
  return path.startsWith('/') || /^[a-z]:\//i.test(path) || /^[a-z][a-z\d+.-]*:\/\//i.test(path);
}

function normalizePath(path: string): string {
  return path.trim().replace(/\\/g, '/');
}

function unique(values: readonly string[]): string[] {
  return [...new Set(values.filter((value) => value.trim().length > 0))];
}

function findOwningBurst(events: readonly ConversationEvent[], warning: SystemParserWarningEvent): number {
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const candidate = events[index];
    if (candidate.runId !== warning.runId) break;
    if (candidate.kind === 'toolBurst') return index;
    if (candidate.kind.startsWith('message.') || candidate.kind === 'runMarker') break;
  }
  return -1;
}

function attachParserDetail(burst: ToolBurstEvent, warning: SystemParserWarningEvent): ToolBurstEvent {
  const detail: ToolCommandExecution = {
    command: 'Parser detail',
    status: 'unknown',
    exitCode: null,
    output: `${warning.message}\nExpected event: ${warning.expectedKind}`,
    outputLineCount: 2,
    outputTruncated: false,
  };
  return {
    ...burst,
    rawRange: { ...burst.rawRange, end: Math.max(burst.rawRange.end, warning.rawRange.end) },
    commands: [...(burst.commands ?? []), detail],
  };
}

function formatCompletion(event: SystemStatusEvent): ConversationEvent[] {
  const match = TOKEN_TOTAL.exec(`${event.label} ${event.explanation}`);
  if (!match) return [event];
  const total = Number(match[1].replace(/[, _]/g, ''));
  if (!Number.isFinite(total)) return [event];
  return [
    {
      ...event,
      category: 'result',
      label: 'Turn completed',
      explanation: '',
      nextStep: undefined,
    },
    {
      id: `${event.id}:usage`,
      kind: 'metric.token',
      timestamp: event.timestamp,
      runId: event.runId,
      model: event.model,
      thinkingLevel: event.thinkingLevel,
      rawRange: event.rawRange,
      scope: 'turn',
      inputTokens: total,
      outputTokens: 0,
    },
  ];
}

export function formatCompactTokens(value: number): string {
  if (value < 1_000) return new Intl.NumberFormat('de-DE').format(value);
  const divisor = value >= 1_000_000 ? 1_000_000 : 1_000;
  const suffix = value >= 1_000_000 ? 'M' : 'k';
  return `${new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1 }).format(value / divisor)}${suffix}`;
}
