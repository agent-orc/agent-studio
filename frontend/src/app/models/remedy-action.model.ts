/**
 * AGT-3580 - the typed way an operator acts on a remedy. Mirrors backend
 * `RemedyAction`: a pickup-hold way out or a readiness remedy names a route,
 * the project setting that owns the fix, or an existing API action, so the UI
 * renders a button or a link. Command text, where a fix has no UI equivalent,
 * travels separately and appears only as a copyable detail.
 */
export interface RemedyAction {
  kind: RemedyActionKind | string;
  /** Route id, project setting key, or API action id, depending on `kind`. */
  target: string;
  project?: string | null;
  /** Stable key of another task the action acts on; absent means the task in view. */
  taskKey?: string | null;
}

export type RemedyActionKind = 'route' | 'setting' | 'api';

/** Backend `RemedyRoutes`. */
export const REMEDY_ROUTES = {
  task: 'task',
  executionHosts: 'execution-hosts',
  gitHubSettings: 'github-settings',
} as const;

/** Backend `ProjectSettingKeys`; the settings page marks each with `data-setting-key`. */
export const PROJECT_SETTING_KEYS = {
  executionRunner: 'execution-runner',
  repositoryVisibility: 'repository-visibility',
  integrationBranch: 'integration-branch',
} as const;

/** Backend `RemedyApiActions`. */
export const REMEDY_API_ACTIONS = {
  setExecutionRunner: 'set-execution-runner',
  editWaitsOn: 'edit-waits-on',
  archiveTask: 'archive-task',
  releaseTask: 'release-task',
  clearDispatchRejection: 'clear-dispatch-rejection',
  startRun: 'start-run',
  setCliType: 'set-cli-type',
  runIntake: 'run-intake',
  moveToEscalated: 'move-to-escalated',
} as const;

export type RemedyApiAction = typeof REMEDY_API_ACTIONS[keyof typeof REMEDY_API_ACTIONS];

const SETTING_LABELS: Record<string, string> = {
  'execution-runner': 'Open execution placement',
  'repository-visibility': 'Open repository visibility',
  'integration-branch': 'Open integration branch',
};

/** Link text for a navigation action; null for an API action, which renders its own button. */
export function remedyLinkLabel(action: RemedyAction): string | null {
  if (action.kind === 'setting') return SETTING_LABELS[action.target] ?? 'Open project setting';
  if (action.kind !== 'route') return null;
  switch (action.target) {
    case REMEDY_ROUTES.task: return action.taskKey ? `Open ${action.taskKey}` : 'Open the task';
    case REMEDY_ROUTES.executionHosts: return 'Open Execution Hosts';
    case REMEDY_ROUTES.gitHubSettings: return 'Open GitHub settings';
    default: return null;
  }
}

/** True when operator-facing remedy text reads as a raw HTTP request line. */
export function startsWithHttpVerb(text: string | null | undefined): boolean {
  return /^\s*(GET|PUT|POST|PATCH|DELETE|HEAD|OPTIONS)\b/.test(text ?? '');
}
