import type {
  ChatNavigationContext,
  OrchestratorContextEnvelope,
  OrchestratorContextReference,
} from './models/orchestrator.model';
import { parseOrchestratorContextKey } from './components/orchestrator-side-sheet/orchestrator-context-key.util';

/** Freeze the conversation scope and active surface at submit time. */
export function buildOrchestratorContextEnvelope(
  contextKey: string,
  navigation: ChatNavigationContext | null,
  explicitReferences: OrchestratorContextReference[] = [],
  now: () => Date = () => new Date(),
): OrchestratorContextEnvelope {
  const parsed = parseOrchestratorContextKey(contextKey);
  if (!parsed?.projectId || parsed.kind === 'global') {
    throw new Error('The selected orchestrator context is invalid.');
  }

  const taskKey = navigation?.currentTaskKey ?? navigation?.currentTaskId ?? null;
  const activeSurface = taskKey
    ? { kind: 'task', reference: taskKey, title: navigation?.currentTaskTitle, taskKey }
    : navigation?.pageRef
      ? {
          kind: navigation.pageType === 'workbench' ? 'workbench' : 'page',
          reference: navigation.pageRef,
          title: navigation.pageTitle,
        }
      : navigation?.observedSurface || navigation?.currentPage
        ? { kind: navigation.observedSurface ?? navigation.currentPage ?? 'project' }
        : null;

  return {
    scope: {
      kind: parsed.kind,
      contextKey,
      projectId: parsed.projectId,
      taskKey: parsed.taskKey,
      workbenchKey: parsed.workbenchKey,
    },
    activeSurface,
    explicitReferences: explicitReferences.map(reference => ({
      ...reference,
      projectId: reference.projectId ?? parsed.projectId,
    })),
    budget: {
      automaticSoftCapTokens: 4_000,
      automaticHardCapTokens: 6_000,
      totalHardCapTokens: 8_000,
      charactersPerEstimatedToken: 4,
    },
    capturedAt: now().toISOString(),
  };
}
