/**
 * Project page shell rail configuration. Drives both the left-rail nav
 * order and the placeholder-panel headers. Slice 2 of the quality-system
 * mockup (docs/mockups/quality-system/) is the source of truth for the
 * inventory and copy below; per-panel real content lands in follow-up
 * slices listed in that mockup's README.
 */
import type { StudioIconName } from '../../../../components/studio-icon/studio-icon.component';

export type ProjectRailGroup = 'insight' | 'quality' | 'context' | 'config';

export type ProjectRailKey =
  | 'overview'
  | 'deploy'
  | 'operations'
  | 'project-urls'
  | 'git'
  | 'integration'
  | 'visual-evidence'
  | 'security'
  | 'architecture'
  | 'project-graph'
  | 'drift'
  | 'uxui'
  | 'test-quality'
  | 'token-usage'
  | 'cycle-time'
  | 'agent-activity'
  | 'observability'
  | 'steering'
  | 'wiki'
  // Nav-rebuild rails that were introduced as reachable project-level
  // destinations and now render real panels where the host supplies them.
  | 'pipeline'
  | 'workflow'
  | 'prompts'
  | 'audits'
  | 'proposals'
  | 'settings'
  | 'settings-defaults'
  | 'settings-overrides'
  | 'ownership-routing'
  | 'orchestrator';

export interface ProjectRailItem {
  key: ProjectRailKey;
  group: ProjectRailGroup;
  /** Visible label in the rail. */
  label: string;
  /** Title shown at the top of the panel; may include the leading glyph. */
  panelTitle: string;
  /** Single-line panel description, lifted from the mockup's section-head. */
  description: string;
  /** Empty-state copy for the placeholder body. */
  empty: string;
  /** Glyph used in the placeholder panel header. */
  icon: string;
  /** SVG glyph used by the shared tree-row navigation control. */
  railIcon?: StudioIconName | null;
  /**
   * When set, this item is a child of the given parent and renders nested /
   * indented under it; the parent gains a disclosure twisty. Children live in
   * the same `group` as their parent.
   */
  parent?: ProjectRailKey;
  /** Whether selecting the row routes to a panel. Defaults to true. */
  navigable?: boolean;
}

export const PROJECT_RAIL_ITEMS: readonly ProjectRailItem[] = [
  // ---- INSIGHT: what the project IS / does ----
  // Order matches the current Deck IA:
  // Overview · Visual Evidence · Drift · Agent activity · Token Usage.
  // Architecture used to live here; it now sits in Context (ASS-1711)
  // because it is thematically a doc surface, not a live-health surface.
  {
    key: 'overview',
    group: 'insight',
    label: 'Overview',
    panelTitle: 'Overview',
    description: 'Operator pulse: delivery, spend, live URLs, deployment, and project knowledge',
    empty: 'No operator overview is available for this project.',
    icon: '▤',
    railIcon: 'layout',
  },
  {
    key: 'project-urls',
    group: 'insight',
    label: 'Project URLs',
    panelTitle: 'Project URLs',
    description: 'Dev-server and preview URLs for this project: status, build/restart, and add',
    empty: 'No URLs configured yet. Add one to see it here and as a row in the Explorer tree.',
    icon: '◍',
    railIcon: 'link',
  },
  {
    key: 'integration',
    group: 'insight',
    label: 'Integration',
    panelTitle: 'Integration',
    description: 'Accepted-card merge queue, curated publisher merges, and the develop-to-main promotion delta',
    empty: 'No integration state is available for this project.',
    icon: '⇉',
    railIcon: 'diff',
  },
  {
    key: 'git',
    group: 'insight',
    label: 'Git View',
    panelTitle: 'Git View',
    description: 'Branches, worktree/checkout folders, and recent history for this project repository',
    empty: 'No git data yet. Once the project has a git repository, its branches, worktrees, and recent commits appear here.',
    icon: '⎇',
    railIcon: 'branch',
  },
  {
    key: 'visual-evidence',
    group: 'insight',
    label: 'Visual Evidence',
    panelTitle: 'Visual Evidence',
    description: 'Project screenshots, UI evidence, and task links',
    empty: 'No visual evidence yet. Screenshots created by Playwright or browser checks will appear here.',
    icon: '◉',
    railIcon: 'eye',
  },
  {
    key: 'drift',
    group: 'insight',
    label: 'Drift',
    panelTitle: 'Drift',
    description: 'Overall drift score, per-dimension state, findings, and follow-ups',
    empty: 'No drift reports yet. Run a comparison to produce the first one.',
    icon: '↯',
    railIcon: 'diff',
  },
  {
    // Agent-work observability stays in the Deck; application health moved
    // to the project-level Operations area.
    key: 'agent-activity',
    group: 'insight',
    label: 'Agent activity',
    panelTitle: 'Agent activity',
    description: 'Agent communication on the message bus, token summary, supervisor and meta-cycle activity, and analysis reports',
    empty: 'No bus messages for this project yet. Once the orchestrator, an agent, or the supervisor speaks, the timeline and counters fill in here.',
    icon: '⌁',
    railIcon: 'message',
  },
  {
    key: 'token-usage',
    group: 'insight',
    label: 'Token Usage',
    panelTitle: 'Token Usage',
    description: 'Inference spend by job, supporting runs, orchestrator turns, and time window',
    empty: 'Token Usage placeholder. Heatmap, timeline, and per-job drill-down land in a later slice.',
    icon: '▦',
    railIcon: 'cli',
  },
  {
    key: 'cycle-time',
    group: 'insight',
    label: 'Throughput & cycle time',
    panelTitle: 'Throughput & cycle time',
    description: 'Machine cycle, resource waits, daily throughput, tokens and cost for completed tasks',
    empty: 'No completed task in the selected window.',
    icon: '◷',
    railIcon: 'activity',
  },

  // ---- QUALITY: what guards the project ----
  // Per mockup order: Security · Test Quality · Audits & Checks.
  {
    key: 'security',
    group: 'quality',
    label: 'Security',
    panelTitle: 'Security',
    description: 'Baseline, reviews, and active findings for this project',
    empty: 'No security baseline yet. The Security slice ships the baseline action and review history.',
    icon: '⊡',
    railIcon: 'warn',
  },
  {
    key: 'test-quality',
    group: 'quality',
    label: 'Test Quality',
    panelTitle: 'Test Quality',
    description: 'Backend tests, end-to-end tests, tuning runs, coverage, and source-code perspective',
    empty: 'Test Quality placeholder. Run history and coverage views land in a later slice.',
    icon: '✓',
    railIcon: 'check',
  },
  {
    key: 'audits',
    group: 'quality',
    label: 'Audits & Checks',
    panelTitle: 'Audits & Checks',
    description: 'Review definitions, per-task checks, and runtime probe slots for this project',
    empty: 'Audits & Checks placeholder. The review-definition model lands in a later slice.',
    icon: '⊟',
    railIcon: 'list',
  },
  {
    key: 'proposals',
    group: 'quality',
    label: 'Proposals',
    panelTitle: 'Project Proposals',
    description: 'Measured findings ready for operator approval and implementation-card spawning',
    empty: 'No proposal generation exists yet.',
    icon: '◇',
    railIcon: 'list',
  },
  // ---- CONTEXT: what agents and humans read to understand the project ----
  {
    key: 'architecture',
    group: 'context',
    label: 'Architecture',
    panelTitle: 'Architecture',
    description: 'Architectural decisions and drift status',
    empty: 'Architecture placeholder. ADR list and high-level map land in the architecture slice.',
    icon: '⊞',
    railIcon: 'layout',
  },
  {
    key: 'project-graph',
    group: 'context',
    label: 'Project Graph',
    panelTitle: 'Project Graph',
    description: 'Read-only component inventory from solution, project, package, Angular workspace, and workflow manifests',
    empty: 'No repository graph is available for this project.',
    icon: '⌘',
    railIcon: 'branch',
  },
  {
    key: 'wiki',
    group: 'context',
    label: 'Wiki',
    panelTitle: 'Wiki',
    description: 'Browse the project wiki: categories, Markdown pages, HTML pages, and accumulated learnings',
    empty: 'No pages found. Once the project has a wiki, its categories and rendered pages appear here.',
    icon: '📚',
    railIcon: 'book',
  },
  {
    // These are the instructions agents read of
    // their own accord (AGENTS.md, frontend/AGENTS.md, the agent-facing
    // domain/nav docs). The key stays 'steering' so deep-links and the
    // shipped Agent Docs panel keep working.
    key: 'steering',
    group: 'context',
    label: 'Agent Docs',
    panelTitle: 'Agent Docs',
    description: 'Instruction files agents read on their own (AGENTS.md and the agent-facing domain docs), with human summary and drift warnings',
    empty: 'No agent-doc inventory yet. The slice lists AGENTS.md, frontend/AGENTS.md, README, ROADMAP, the task contract, the skills lookup, and the ADR archive.',
    icon: '🧭',
    railIcon: 'file',
  },
  {
    key: 'prompts',
    group: 'context',
    label: 'Prompts',
    panelTitle: 'Prompts',
    description: 'Prompt registry for this project: inventory, source / override matrix, and coverage.',
    empty: 'Prompts shell — navigation only. Step 2 (T5b) moves the prompt-admin surface here unchanged: registry inventory, source/override matrix, and coverage.',
    icon: '✎',
    railIcon: 'file',
  },

  // ---- CONFIG: how the project is set up ----
  // Workflow and Settings remain in Deck Config; Pipeline moved to Deploy.
  {
    key: 'workflow',
    group: 'config',
    label: 'Workflow',
    panelTitle: 'Workflow / Lanes',
    description: 'The lane model and per-lane ordering, plus a read-only view of what the platform does at each transition today. Per-transition Git integration comes after the Git concept decision.',
    empty: 'Workflow / Lanes — the lane list, per-lane sort order, and a read-only transition view. Per-transition Git profiles and MR / team workflow stay placeholders until the Git concept is decided.',
    icon: '⇄',
    railIcon: 'branch',
  },
  {
    key: 'orchestrator',
    group: 'config',
    label: 'Orchestrator',
    panelTitle: 'Orchestrator',
    description: 'Live session, recent decisions and observations',
    empty: 'Orchestrator placeholder. Session detail and recent decisions land in a later slice.',
    icon: '◈',
    railIcon: 'bot',
  },
  {
    // Settings is a navigable tree parent: the row opens the full Settings
    // panel, and the twisty expands to its grouped sub-areas below.
    key: 'settings',
    group: 'config',
    label: 'Settings',
    panelTitle: 'Settings',
    description: 'How the orchestrator behaves on this project',
    empty: 'Settings placeholder. Runner mode, auto-commit, and orchestrator model controls arrive next.',
    icon: '⚙',
    railIcon: 'settings',
  },
  {
    key: 'settings-defaults',
    group: 'config',
    parent: 'settings',
    label: 'Workspace Defaults',
    panelTitle: 'Settings',
    description: 'Global defaults inherited from Workspace settings (default agent, usage caps)',
    empty: 'Workspace defaults placeholder.',
    icon: '⚙',
    railIcon: 'sliders',
  },
  {
    key: 'settings-overrides',
    group: 'config',
    parent: 'settings',
    label: 'Project Overrides',
    panelTitle: 'Settings',
    description: 'Per-project settings that win over the inherited workspace defaults',
    empty: 'Project overrides placeholder.',
    icon: '⚙',
    railIcon: 'settings',
  },
  {
    key: 'ownership-routing',
    group: 'config',
    parent: 'settings',
    label: 'Ownership Routing',
    panelTitle: 'Component ownership and delivery',
    description: 'Primary owners, consumers, packages, releases, and deployment chains',
    empty: 'No ownership mappings are configured.',
    icon: '⇢',
    railIcon: 'branch',
  },
];

/** Keys that have at least one child (i.e. render a disclosure twisty). */
export const PROJECT_RAIL_PARENT_KEYS: readonly ProjectRailKey[] = Array.from(
  new Set(PROJECT_RAIL_ITEMS.filter(i => i.parent).map(i => i.parent as ProjectRailKey)),
);

// These remain valid bookmark destinations while their panels live in the
// project-level Deploy and Operations areas instead of the Deck rail.
const RAIL_KEY_SET = new Set<string>([
  ...PROJECT_RAIL_ITEMS.map(i => i.key),
  'deploy', 'operations', 'pipeline', 'observability',
]);

export function isProjectRailKey(value: string | null | undefined): value is ProjectRailKey {
  return !!value && RAIL_KEY_SET.has(value);
}

export const DEFAULT_PROJECT_RAIL_KEY: ProjectRailKey = 'overview';

/** Deck rail and Deploy view that a requested section opens. */
export interface ProjectSectionTarget {
  rail: ProjectRailKey;
  /** `steps` opens the Run steps view of the Deploy area. */
  deployView: 'overview' | 'steps';
}

/**
 * Resolve a requested section, including retired names, to the area that
 * now hosts it: `deployment` and `pipeline` open Deploy (`pipeline` on its
 * Run steps view), and `observability` opens Operations.
 */
export function resolveProjectSection(section: string | null | undefined): ProjectSectionTarget {
  if (section === 'deploy' || section === 'deployment' || section === 'pipeline') {
    return { rail: 'deploy', deployView: section === 'pipeline' ? 'steps' : 'overview' };
  }
  if (section === 'observability') return { rail: 'operations', deployView: 'overview' };
  return { rail: isProjectRailKey(section) ? section : DEFAULT_PROJECT_RAIL_KEY, deployView: 'overview' };
}

/** Canonical user-facing label for a Deck rail section. */
export function projectRailLabel(section: string | null | undefined): string {
  if (section === 'deploy' || section === 'deployment' || section === 'pipeline') return 'Deploy';
  if (section === 'operations' || section === 'observability') return 'Operations';
  const key = isProjectRailKey(section) ? section : DEFAULT_PROJECT_RAIL_KEY;
  return PROJECT_RAIL_ITEMS.find(item => item.key === key)?.label ?? 'Overview';
}

/**
 * Slug used in the project-shell URL hash. Stable mapping from a watch-path
 * project name (e.g. "Agent Software Studio") to a kebab-case identifier
 * (e.g. "agent-software-studio"). The reverse lookup happens by computing
 * the slug for every known watch path and matching.
 */
export function toProjectSlug(name: string): string {
  return name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}
