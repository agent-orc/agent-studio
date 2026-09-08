import { describe, expect, it } from 'vitest';
import { buildChatNavigationContext } from './chat-navigation-context';
import { buildOrchestratorContextEnvelope } from './orchestrator-context-envelope';

/**
 * Pure-function unit test for the navigation-context builder that ships
 * on every project-chat POST. The shape is part of the chat agent's
 * contract: missing or stale fields produced the 2026-05-09 "Conversation,
 * Foul Conversation" hallucination. This locks the routing rules so future
 * router refactors cannot silently strip the context.
 */
describe('buildChatNavigationContext', () => {
  const fixedNow = () => new Date('2026-05-09T08:42:00.000Z');

  it('marks task-detail when a job id is active and forwards id + title', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-auto-review-reorder-drops-card',
      activeTaskKey: 'AGT-2517',
      activeJobTitle: 'Bug: reordering a card inside auto-review drops it from the lane',
      now: fixedNow
    });

    expect(ctx.currentPage).toBe('task-detail');
    expect(ctx.currentTaskId).toBe('bug-auto-review-reorder-drops-card');
    expect(ctx.currentTaskKey).toBe('AGT-2517');
    expect(ctx.currentTaskTitle).toBe(
      'Bug: reordering a card inside auto-review drops it from the lane'
    );
    expect(ctx.viewportTimestamp).toBe('2026-05-09T08:42:00.000Z');
  });

  it('forwards optional task state and lane filter when supplied', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-X',
      activeJobTitle: 'Bug X',
      activeJobState: '4-auto-review',
      laneFilter: '4-auto-review',
      now: fixedNow
    });
    expect(ctx.currentTaskState).toBe('4-auto-review');
    expect(ctx.currentLaneFilter).toBe('4-auto-review');
  });

  it('defaults to kanban-board when no task is active and omits task fields', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: null,
      activeJobTitle: null,
      now: fixedNow
    });
    expect(ctx.currentPage).toBe('kanban-board');
    expect(ctx.currentTaskId).toBeUndefined();
    expect(ctx.currentTaskKey).toBeUndefined();
    expect(ctx.currentTaskTitle).toBeUndefined();
    expect(ctx.viewportTimestamp).toBe('2026-05-09T08:42:00.000Z');
  });

  it('uses the stable task key to retain task scope before detail hydration catches up', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: null,
      activeTaskKey: 'QS-54',
      activeJobTitle: null,
      now: fixedNow,
    });

    expect(ctx.currentPage).toBe('task-detail');
    expect(ctx.currentTaskKey).toBe('QS-54');
    expect(ctx.currentTaskId).toBeUndefined();
  });

  it('treats whitespace-only ids as absent', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: '   ',
      activeJobTitle: '',
      now: fixedNow
    });
    expect(ctx.currentPage).toBe('kanban-board');
    expect(ctx.currentTaskId).toBeUndefined();
    expect(ctx.currentTaskTitle).toBeUndefined();
  });

  it('stamps the current wall-clock viewport timestamp when no now() override is given', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'bug-X',
      activeJobTitle: 'Bug X'
    });
    expect(typeof ctx.viewportTimestamp).toBe('string');
    expect(ctx.viewportTimestamp).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/);
  });

  it('embeds a repository page in project navigation context without inventing a task', () => {
    const ctx = buildChatNavigationContext({
      activeJobId: 'stale-task',
      activeJobTitle: 'Stale task',
      pageContext: {
        projectName: 'PROJ-002',
        relPath: 'concepts/action-bar.md',
        title: 'Action bar',
        pageType: 'concept',
        excerpt: 'Pages are bidirectional interfaces.',
      },
      now: fixedNow,
    });

    expect(ctx.currentPage).toBe('repository-page');
    expect(ctx.pageRef).toBe('page:PROJ-002/concepts/action-bar.md');
    expect(ctx.pageType).toBe('concept');
    expect(ctx.pageExcerpt).toBe('Pages are bidirectional interfaces.');
    expect(ctx.currentTaskId).toBeUndefined();
  });
});

describe('buildOrchestratorContextEnvelope', () => {
  const fixedNow = () => new Date('2026-08-10T10:15:00.000Z');

  it('freezes task scope, active surface, and dossier budget defaults', () => {
    const navigation = buildChatNavigationContext({
      activeJobId: 'task-folder',
      activeTaskKey: 'AGT-2572',
      activeJobTitle: 'Context foundation',
      now: fixedNow,
    });

    expect(buildOrchestratorContextEnvelope(
      'task:Agent Studio/AGT-2572', navigation, [], fixedNow,
    )).toEqual({
      scope: {
        kind: 'task',
        contextKey: 'task:Agent Studio/AGT-2572',
        projectId: 'Agent Studio',
        taskKey: 'AGT-2572',
        workbenchKey: null,
      },
      activeSurface: {
        kind: 'task',
        reference: 'AGT-2572',
        title: 'Context foundation',
        taskKey: 'AGT-2572',
      },
      explicitReferences: [],
      budget: {
        automaticSoftCapTokens: 4_000,
        automaticHardCapTokens: 6_000,
        totalHardCapTokens: 8_000,
        charactersPerEstimatedToken: 4,
      },
      capturedAt: '2026-08-10T10:15:00.000Z',
    });
  });

  it('freezes a Dossier (workbench) scope with its catalogue key and no task key', () => {
    const envelope = buildOrchestratorContextEnvelope(
      'workbench:Agent Studio/AGT-W43', null, [], fixedNow,
    );

    expect(envelope.scope).toEqual({
      kind: 'workbench',
      contextKey: 'workbench:Agent Studio/AGT-W43',
      projectId: 'Agent Studio',
      taskKey: null,
      workbenchKey: 'AGT-W43',
    });
  });

  it('pins explicit references to the active project', () => {
    const envelope = buildOrchestratorContextEnvelope(
      'project:Agent Studio',
      null,
      [{ kind: 'repository-file', reference: 'docs/start/README.md' }],
      fixedNow,
    );

    expect(envelope.scope.kind).toBe('project');
    expect(envelope.explicitReferences[0].projectId).toBe('Agent Studio');
  });

  it('rejects invalid context keys before the network call starts', () => {
    expect(() => buildOrchestratorContextEnvelope('global', null, [], fixedNow))
      .toThrow('selected orchestrator context is invalid');
  });
});
