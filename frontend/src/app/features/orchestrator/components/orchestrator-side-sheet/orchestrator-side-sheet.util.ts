import type { OrchestratorChatTurn } from '../../../../features/orchestrator';
import type { ChatEvent, ConversationEvent, RawLineRange } from 'coding-agent-chat/core';

/**
 * Pure helpers for the orchestrator side sheet. Extracted from the
 * component controller so the component .ts stays within its size budget
 * while the navigation-context / pin logic (MC-2) lives inline where it
 * belongs. These functions carry no Angular dependency and are unit-tested
 * directly.
 */

/**
 * Hide any server user turn that an in-flight local turn already represents.
 *
 * After the operator hits Send we render a local "optimistic" turn so the
 * bubble shows up immediately (including the inline blob preview of any
 * attached image). When the round-trip to the orchestrator finishes the
 * server now reports the same user turn back, but the local turn is still
 * on screen until the persisted attachment URL has been pre-decoded into
 * the browser image cache. Without this dedup, the user would see the
 * bubble briefly duplicate during that pre-decode window.
 *
 * Match strategy: walk local user turns newest-to-oldest and pair each
 * with the newest unmatched server user turn that has the same text and
 * the same number of attachments. Pairing is greedy and one-shot so
 * sending the same message twice in a row only suppresses one copy per
 * local turn.
 */
export function suppressLocalDuplicates(
  server: readonly OrchestratorChatTurn[],
  local: readonly (OrchestratorChatTurn & { localAttachments?: { alt: string; previewUrl: string }[] })[]
): OrchestratorChatTurn[] {
  if (local.length === 0) return [...server];
  const localUsers = local.filter((t) => t.role === 'user');
  if (localUsers.length === 0) return [...server];
  const suppress = new Set<string>();
  for (const lt of localUsers) {
    const ltAttCount = lt.localAttachments?.length ?? lt.attachments?.length ?? 0;
    for (let i = server.length - 1; i >= 0; i--) {
      const st = server[i];
      if (suppress.has(st.id)) continue;
      if (st.role !== 'user') continue;
      if ((st.text ?? '') !== (lt.text ?? '')) continue;
      const stAttCount = st.attachments?.length ?? 0;
      if (stAttCount !== ltAttCount) continue;
      suppress.add(st.id);
      break;
    }
  }
  return suppress.size === 0 ? [...server] : server.filter((s) => !suppress.has(s.id));
}

export type OptimisticOrchestratorChatTurn = OrchestratorChatTurn & {
  pending?: boolean;
  localAttachments?: { alt: string; previewUrl: string }[];
};

/**
 * Signal equality for the polled server transcript.
 *
 * The endpoint deserializes a fresh object graph on every heartbeat even when
 * no turn changed. Treating that graph as new makes the conversation projection
 * and every Markdown host run again; Studio's task-reference hydrator then has
 * to replace the same AGT-* text with the same microcards, which is visible as
 * selective pill flicker. Compare the complete persisted turn contract so a
 * real text, metadata, token, error, or attachment change still propagates.
 */
export function sameOrchestratorChatTurns(
  previous: readonly OrchestratorChatTurn[],
  current: readonly OrchestratorChatTurn[],
): boolean {
  if (previous === current) return true;
  if (previous.length !== current.length) return false;
  return previous.every((left, index) => {
    const right = current[index];
    return left.id === right.id
      && left.ts === right.ts
      && left.role === right.role
      && left.text === right.text
      && (left.model ?? null) === (right.model ?? null)
      && (left.errorMessage ?? null) === (right.errorMessage ?? null)
      && sameContextReceipt(left.contextReceipt, right.contextReceipt)
      && sameTokenUsage(left.tokenUsage, right.tokenUsage)
      && sameAttachments(left.attachments, right.attachments);
  });
}

function sameContextReceipt(
  left: OrchestratorChatTurn['contextReceipt'],
  right: OrchestratorChatTurn['contextReceipt'],
): boolean {
  if (left === right) return true;
  if (!left || !right) return left == null && right == null;
  return left.scope === right.scope
    && left.contextKey === right.contextKey
    && (left.taskKey ?? null) === (right.taskKey ?? null)
    && left.capturedAt === right.capturedAt
    && left.includedBlocks.length === right.includedBlocks.length
    && left.includedBlocks.every((block, index) => block === right.includedBlocks[index]);
}

function sameTokenUsage(
  left: OrchestratorChatTurn['tokenUsage'],
  right: OrchestratorChatTurn['tokenUsage'],
): boolean {
  if (left === right) return true;
  if (!left || !right) return left == null && right == null;
  return (left.model ?? null) === (right.model ?? null)
    && (left.thinkingLevel ?? null) === (right.thinkingLevel ?? null)
    && left.inputTokens === right.inputTokens
    && left.outputTokens === right.outputTokens
    && left.cacheReadTokens === right.cacheReadTokens
    && left.cacheCreationTokens === right.cacheCreationTokens;
}

function sameAttachments(
  left: OrchestratorChatTurn['attachments'],
  right: OrchestratorChatTurn['attachments'],
): boolean {
  if (left === right) return true;
  if (!left || !right) return left == null && right == null;
  if (left.length !== right.length) return false;
  return left.every((attachment, index) => {
    const candidate = right[index];
    return attachment.alt === candidate.alt
      && attachment.relativePath === candidate.relativePath
      && (attachment.inlineBase64 ?? null) === (candidate.inlineBase64 ?? null)
      && (attachment.mimeType ?? null) === (candidate.mimeType ?? null);
  });
}

/**
 * Project the side sheet's transport-specific transcript into the canonical
 * `coding-agent-chat` conversation grammar.
 *
 * The orchestrator endpoint returns simple user/orchestrator turns while the
 * optimistic path temporarily holds a second, local representation of the
 * newest user turn. The next-gen conversation view must receive one ordered
 * event stream, so this adapter suppresses that overlap, resolves both local
 * and persisted attachment URLs, and translates the legacy inline event-card
 * contract into the closest semantic `ConversationEvent` kind.
 */
export function buildOrchestratorConversationEvents(
  serverTurns: readonly OrchestratorChatTurn[],
  localTurns: readonly OptimisticOrchestratorChatTurn[],
  inlineEvents: readonly ChatEvent[],
  projectName: string | null,
  source: string,
  showMetadata = true,
): ConversationEvent[] {
  const persisted = suppressLocalDuplicates(serverTurns, localTurns);
  const turns: readonly OptimisticOrchestratorChatTurn[] = [...persisted, ...localTurns];
  const projected: { event: ConversationEvent; inputIndex: number }[] = [];

  turns.forEach((turn, index) => {
    const localAttachments = turn.localAttachments?.map(attachment => ({
      alt: attachment.alt,
      url: attachment.previewUrl,
    })) ?? [];
    const persistedAttachments = (turn.attachments ?? []).map(attachment => ({
      alt: attachment.alt,
      url: resolveAttachmentUrl(projectName, attachment.relativePath),
    }));
    const attachments = localAttachments.length > 0 ? localAttachments : persistedAttachments;
    const error = turn.errorMessage?.trim();
    let body = error
      ? `${turn.text ? `${turn.text}\n\n` : ''}**Error:** ${error}`
      : turn.text;
    if (showMetadata && turn.role === 'orchestrator' && turn.metadata) {
      const meta = turn.metadata;
      const usage = turn.tokenUsage;
      const tokenCount = usage
        ? usage.inputTokens + usage.outputTokens + usage.cacheReadTokens + usage.cacheCreationTokens
        : null;
      const parts = [
        meta.model && [meta.model, meta.effort].filter(Boolean).join(' · '),
        tokenCount !== null && `${tokenCount.toLocaleString('en-US')} tokens`,
        meta.cost != null && `est. ${meta.currency === 'USD' ? '$' : `${meta.currency ?? ''} `}${meta.cost.toFixed(4)}`,
        `${(meta.totalLatencyMs / 1000).toFixed(1)}s total`,
        meta.queueLatencyMs != null && `${(meta.queueLatencyMs / 1000).toFixed(1)}s queued`,
        meta.host,
        meta.providerSessionId && `session ${meta.providerSessionId.slice(0, 8)}`,
      ].filter(Boolean);
      body += `\n\n*${parts.join(' · ')}*`;
    }

    projected.push({
      inputIndex: index,
      event: {
        id: turn.id,
        kind: turn.role === 'user' ? 'message.user' : 'message.orchestrator',
        timestamp: turn.ts,
        severity: error ? 'error' : undefined,
        model: turn.model ?? turn.tokenUsage?.model ?? null,
        thinkingLevel: turn.tokenUsage?.thinkingLevel ?? null,
        rawRange: rangeFor(source, index),
        body,
        actor: turn.role === 'user' ? 'You' : 'Orchestrator',
      },
    });

    attachments.forEach((attachment, attachmentIndex) => {
      projected.push({
        inputIndex: index + (attachmentIndex + 1) / (attachments.length + 1),
        event: {
          id: `${turn.id}:attachment:${attachmentIndex}`,
          kind: 'artifact.image',
          timestamp: turn.ts,
          rawRange: rangeFor(source, index),
          caption: attachment.alt,
          url: attachment.url,
          sourcePath: attachment.url,
          durablePath: null,
          sourceTool: 'orchestrator-chat',
        },
      });
    });
  });

  inlineEvents.forEach((event, index) => {
    const inputIndex = turns.length + index;
    projected.push({
      inputIndex,
      event: projectInlineEvent(event, rangeFor(source, inputIndex)),
    });
  });

  return projected
    .sort((left, right) => compareTimestamp(left.event.timestamp, right.event.timestamp)
      || left.inputIndex - right.inputIndex)
    .map(item => item.event);
}

function projectInlineEvent(event: ChatEvent, rawRange: RawLineRange): ConversationEvent {
  const base = {
    id: event.id,
    timestamp: event.timestamp,
    severity: event.severity,
    rawRange,
  } as const;

  switch (event.kind) {
    case 'tool-call':
      return {
        ...base,
        kind: 'toolBurst',
        count: 1,
        families: { other: 1 },
        failures: event.severity === 'error' ? 1 : 0,
        durationMs: 0,
        samples: { other: event.summary },
        collapsedByDefault: true,
      };
    case 'watchdog':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: event.severity === 'error' || /\bkill(?:ed)?\b/i.test(event.summary) ? 'killed' : 'quiet',
        quietSeconds: secondsFrom(event.summary),
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'session-recovered':
      return {
        ...base,
        kind: 'supervisor.wait',
        state: 'resumed',
        quietSeconds: 0,
        reason: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
      };
    case 'decision':
      return {
        ...base,
        kind: 'decision.orchestrator',
        decisionType: event.decisionType ?? 'decision',
        reason: event.summary,
        evidence: event.detail,
        action: event.actionLabel,
      };
    case 'rate-limit':
    case 'update':
    case 'task':
    case 'memory-refreshed':
      return {
        ...base,
        kind: 'system.status',
        category: event.kind,
        label: inlineEventLabel(event.kind),
        explanation: event.detail ? `${event.summary}\n\n${event.detail}` : event.summary,
        nextStep: event.actionLabel,
      };
  }
}

function rangeFor(source: string, index: number): RawLineRange {
  const line = index + 1;
  return { source, start: line, end: line };
}

function compareTimestamp(left: string, right: string): number {
  const leftMs = Date.parse(left);
  const rightMs = Date.parse(right);
  if (!Number.isFinite(leftMs) || !Number.isFinite(rightMs)) return 0;
  return leftMs - rightMs;
}

function secondsFrom(summary: string): number {
  const match = /\b(\d+)\s*s(?:ec(?:ond)?s?)?\b/i.exec(summary);
  return match ? Number(match[1]) : 0;
}

function inlineEventLabel(kind: Extract<ChatEvent['kind'], 'rate-limit' | 'update' | 'task' | 'memory-refreshed'>): string {
  switch (kind) {
    case 'rate-limit': return 'Rate limit';
    case 'update': return 'Update';
    case 'task': return 'Task';
    case 'memory-refreshed': return 'Memory refreshed';
  }
}

/**
 * Resolve a persisted chat attachment's relative path to the GET endpoint
 * that actually serves the bytes. Server returns `chat-attachments/<file>`;
 * we strip that prefix and route through the per-project attachments route
 * so the `<img>` in the bubble loads. Returns the input unchanged when the
 * project or path is missing.
 */
export function resolveAttachmentUrl(projectName: string | null, relativePath: string): string {
  if (!projectName || !relativePath) return relativePath;
  const fileName = relativePath.startsWith('chat-attachments/')
    ? relativePath.substring('chat-attachments/'.length)
    : relativePath;
  return `/api/runner/${encodeURIComponent(projectName)}/orchestrator-chat/attachments/${encodeURIComponent(fileName)}`;
}
