export interface SecurityMeta {
  lastReviewDate: string | null;
  rating: string | null;
  summary: string | null;
}

export interface SecurityFileEntry {
  name: string;
  relPath: string;
  updatedAt: string;
  size: number;
}

export interface SecurityOverview {
  projectName: string;
  baseDir: string;
  exists: boolean;
  meta: SecurityMeta;
  files: SecurityFileEntry[];
}

export interface SecurityFileContent {
  relPath: string;
  content: string;
}

export interface WikiFileEntry {
  name: string;
  relPath: string;
  title: string;
  updatedAt: string;
  size: number;
}

export interface WikiOverview {
  projectName: string;
  baseDir: string;
  exists: boolean;
  files: WikiFileEntry[];
}

export interface WikiFileContent {
  relPath: string;
  content: string;
}

export interface WikiFileSaveResult {
  relPath: string;
  saved: boolean;
  changed: boolean;
  sha: string | null;
  branch: string | null;
}

/** Per-document provenance parsed from YAML frontmatter (mirrors backend WikiDocMetadata). */
export interface WikiDocMetadata {
  model: string | null;
  updatedAt: string | null;
  reason: string | null;
  taskKey: string | null;
  status: string | null;
  runCount: string | null;
  hasFrontmatter: boolean;
}

/** One git commit that touched a wiki doc (mirrors backend GitCommitInfo). */
export interface WikiCommitInfo {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

/** Provenance + git history for a single wiki document. */
export interface WikiFileHistory {
  relPath: string;
  model: string | null;
  metadata: WikiDocMetadata;
  commits: WikiCommitInfo[];
  relatedTasks?: RelatedTaskReference[];
}

export interface RelatedTaskReference {
  key: string;
  title: string;
  linkedAt: string;
  source: 'auto' | 'manual';
  exists: boolean | null;
}

/** Kind of a wiki tree node: a folder, or a document by source type. */
export type WikiNodeType = 'folder' | 'md' | 'html' | 'json';

/** Curated consolidation status of a wiki page. */
export type WikiClassificationStatus = 'aktuell' | 'veraltet' | 'ueberholt' | 'archived';

/**
 * Curation classification of one wiki page (mirrors backend
 * `WikiClassification`): read from the companion sidecar's `classification`
 * block, with the backend filling the `type` from a per-folder default when a
 * page has no sidecar. Absent/null on folders and unclassified pages.
 */
export interface WikiClassification {
  status: WikiClassificationStatus | string | null;
  /** Docs-relative path of the successor page when status is `ueberholt`. */
  supersededBy: string | null;
  /** konzept | adr | contract | domain-map | analyse | runbook | workbench | mockup | proposal | generiert | index */
  type: string | null;
  /** Canonical interactive page kind derived by the backend. */
  pageType?: 'doc' | 'concept' | 'workbench' | 'incident' | 'report' | null;
  /** ISO date of the consolidation analysis. */
  analyzedAt: string | null;
}

/** One retained agent tool-use read in the adjacent companion. */
export interface WikiAgentReadRecent {
  at: string;
  taskKey: string;
}

/** Durable observed read count for one wiki page. Never a drift/gate signal. */
export interface WikiAgentReads {
  total: number;
  lastReadAt: string | null;
  recent: WikiAgentReadRecent[];
}

/** Compact per-document metadata shown in the tree (mirrors backend WikiTreeMetadata). */
export interface WikiTreeMetadata {
  documentMode: string | null;
  temporalState: string | null;
  implementationState: string | null;
  driftGrade: string | null;
  hasDrift: boolean | null;
  driftScore: number | null;
  quality: string | null;
  duplicateSuspected: boolean | null;
  duplicateGroupSize: number | null;
  reportPath: string | null;
  summary: string | null;
  companionPath: string | null;
  sourceChangedSinceReview: boolean | null;
  findingsCount: number | null;
  agentReads?: WikiAgentReads | null;
}

/**
 * One node in the physical wiki tree (mirrors backend WikiTreeNode). A `folder`
 * carries `children`; a document node (`md` / `html` / `json`) is a leaf whose
 * `relPath` is the docs-root-relative path. `title` is the display label (first
 * H1 or JSON title for docs, order-prefix-stripped name otherwise). `metadata`
 * is present only when an adjacent `<source-file>.meta.json` companion
 * describes the source document.
 */
export interface WikiTreeNode {
  name: string;
  title: string;
  relPath: string | null;
  type: WikiNodeType;
  children: WikiTreeNode[];
  metadata?: WikiTreeMetadata | null;
  /** Curated classification (pages only; null for folders and unclassified pages). */
  classification?: WikiClassification | null;
}

/** The physical docs/ folder tree backing the wiki navigation. */
export interface WikiTree {
  projectName: string;
  baseDir: string;
  exists: boolean;
  root: WikiTreeNode[];
  source?: WikiSourceInfo | null;
}

export interface WikiSourceInfo {
  mode: 'checkout' | 'branch';
  branch: string;
  commit: string | null;
  shortCommit: string | null;
  writable: boolean;
  error: string | null;
}

/** One recently-edited wiki page (page / git author / when), newest first. */
export interface WikiRecentEdit {
  relPath: string;
  title: string;
  author: string;
  authorDateUtc: string;
  sha: string;
  shortSha: string;
  subject: string;
}

/** Recent-edits payload backing the wiki dashboard landing surface. */
export interface WikiRecentEdits {
  projectName: string;
  baseDir: string;
  exists: boolean;
  edits: WikiRecentEdit[];
}

/** Content of a wiki doc at a past commit (the "view old revision" payload). */
export interface WikiRevisionContent {
  relPath: string;
  sha: string;
  content: string;
}

export type WorkbenchStatus = 'active' | 'decision-pending' | 'decided' | 'documented' | 'archived' | 'invalid';
export type WorkbenchDecisionStage = 'prepared' | 'pending' | 'failed' | 'succeeded' | 'archived';
export type ArticlePattern = 'ui' | 'concept';

export interface WorkbenchTaskDraft {
  title: string;
  goal: string;
  acceptanceCriteria: string[];
  evidenceLinks: string[];
  chosenOption: string | null;
  relatedTaskKeys: string[];
  targetProject: string | null;
  initialLane: '1-preparation' | '2-ready';
  mode: 'coding';
  taskType: 'feature';
}

export type WorkbenchDecisionKind = 'single' | 'multi' | 'confirm';

/** One author-defined option discovered in the Dossier HTML. */
export interface WorkbenchDecisionOption {
  id: string;
  label: string;
}

/** Read-only definition discovered from the progressive-enhancement markup. */
export interface WorkbenchDecisionPoint {
  id: string;
  kind: WorkbenchDecisionKind;
  label: string;
  options: WorkbenchDecisionOption[];
  commentLabel: string | null;
}

/** Operator input persisted in the descriptor's decision receipt. */
export interface WorkbenchDecisionResponse {
  decisionId: string;
  kind: WorkbenchDecisionKind;
  selectedOptionIds: string[];
  comment: string | null;
}

/** The durable decision receipt stored inside `workbench.json` (schema v2). */
export interface WorkbenchDecisionProjection {
  outcome: 'feature-spawn' | 'rework' | 'archive';
  action?: 'created-card' | 'rework-requested' | 'archived' | null;
  state: 'pending' | 'failed' | 'succeeded';
  operationId: string;
  sourceRevision: string | null;
  sourceFingerprint: string | null;
  preparedAt: string;
  preparedBy: string;
  confirmedAt: string | null;
  confirmedBy: string | null;
  decidedAt: string | null;
  reason: string | null;
  failure: string | null;
  spawnedTaskKeys: string[];
  responses: WorkbenchDecisionResponse[];
  taskDraft: WorkbenchTaskDraft | null;
  cliType?: string | null;
  model?: string | null;
  thinkingLevel?: string | null;
  sourceEntryFingerprint?: string | null;
}

export interface WorkbenchDecisionResult {
  success: boolean;
  errorCode: string | null;
  error: string | null;
  workbenchId: string;
  operationId: string;
  outcome: 'feature-spawn' | 'rework' | 'archive' | null;
  decisionStage: WorkbenchDecisionStage | null;
  revision: string | null;
  fingerprint: string | null;
  spawnedTaskKeys: string[];
  responses: WorkbenchDecisionResponse[];
  idempotent: boolean;
  /**
   * The server-validated task draft for a feature decision. The backend never
   * creates the card itself: the client owns task creation through the existing
   * task API and may report the resulting keys back via `spawnedTaskKeys`.
   */
  taskDraft?: WorkbenchTaskDraft | null;
}

export interface PrepareWorkbenchDecisionRequest {
  operationId: string;
  outcome: 'feature-spawn' | 'rework' | 'archive';
  expectedRevision: string | null;
  expectedFingerprint: string | null;
  actor: string;
  archiveReason: string | null;
  task: WorkbenchTaskDraft | null;
  responses: WorkbenchDecisionResponse[];
}

/**
 * Confirm carries the same decision payload as prepare: prepare only validates
 * and fingerprints (it writes nothing), so confirm is the single durable write.
 */
export interface ConfirmWorkbenchDecisionRequest {
  operationId: string;
  outcome: 'feature-spawn' | 'rework' | 'archive';
  expectedRevision: string | null;
  expectedFingerprint: string | null;
  actor: string;
  archiveReason: string | null;
  task: WorkbenchTaskDraft | null;
  responses: WorkbenchDecisionResponse[];
  /** Keys of cards the client already created for this decision, if any. */
  spawnedTaskKeys?: string[];
  cliType?: string | null;
  model?: string | null;
  thinkingLevel?: string | null;
  confirmed: true;
}

export interface WorkbenchListItem {
  id: string;
  /** Stable project-scoped reference key. Null for legacy entries or a read-only source awaiting checkout backfill. */
  key?: string | null;
  title: string;
  summary: string;
  status: WorkbenchStatus;
  phase: 'shaping' | 'testing' | 'decision-ready' | null;
  updatedAtUtc: string;
  entryPath: string;
  valid: boolean;
  error: string | null;
  sourceTaskKeys: string[];
  /** Presentation variant. Missing or unknown server values render as concept. */
  pattern?: ArticlePattern | null;
  /** Legacy descriptor bridge. Derived card references use the keyed endpoint. */
  relatedTaskKeys?: string[];
  /** Shared lifecycle projection. Present on schema-v2 descriptors. */
  lifecycleState?: WikiLifecycleState | null;
  editedBy?: string | null;
  lifecycleHistory?: WikiLifecycleHistoryEntry[] | null;
  decision?: WorkbenchDecisionProjection | null;
  decisionStage?: WorkbenchDecisionStage | null;
  /** Dossier-level gate count until inline decision points extend it. */
  openDecisionCount?: number;
  documentation?: WorkbenchDocumentationProjection | null;
}

export interface WorkbenchDocumentationReference {
  key: string;
  exists: boolean;
  terminal: boolean;
  lane: string | null;
}

export interface WorkbenchDocumentationProjection {
  eligible: boolean;
  totalCount: number;
  terminalCount: number;
  openCount: number;
  missingCount: number;
  references: WorkbenchDocumentationReference[];
}

export interface DocumentWorkbenchRequest {
  actor: string;
  expectedRevision: string | null;
  expectedFingerprint: string | null;
}

export interface DocumentWorkbenchResult {
  success: boolean;
  errorCode: string | null;
  error: string | null;
  workbenchId: string;
  status: 'documented' | '';
  revision: string | null;
  fingerprint: string | null;
  idempotent: boolean;
}

export interface WorkbenchCatalogue {
  projectName: string;
  includesHistory: boolean;
  count: number;
  items: WorkbenchListItem[];
}

export interface WorkbenchOverviewItem {
  projectName: string;
  workbench: WorkbenchListItem;
}

export interface WorkbenchOverview {
  projectName: string | null;
  count: number;
  currentCount: number;
  historyCount: number;
  items: WorkbenchOverviewItem[];
}

export type WorkbenchHubEventType =
  | 'created'
  | 'updated'
  | 'decisionRecorded'
  | 'statusChanged'
  | 'reconnected';

export interface WorkbenchHubEvent {
  type: WorkbenchHubEventType;
  projectName: string | null;
  workbenchId: string | null;
  workbench: WorkbenchListItem | null;
  previousStatus: WorkbenchStatus | null;
  occurredAtUtc: string;
}

export interface WorkbenchDocument {
  workbench: WorkbenchListItem;
  html: string;
  branch: string | null;
  revision: string | null;
  workingTreeModified: boolean;
  fingerprint: string | null;
  entryFingerprint?: string | null;
}

export interface WorkbenchTaskReferences {
  projectName: string;
  workbenchKey: string;
  workbenchId: string;
  legacyTaskKeys: string[];
  items: {
    sourceKey: string | null;
    sourceJobId: string;
    sourceTitle: string;
    sourceState: string;
    sourceWatchPath: string;
    kind: 'workbenches';
  }[];
}

// ---- Wiki Pulse (PULSE-1: the generated wiki landing view) ----

/** One change-feed row: a recently-edited page + top-folder badge + task key. */
export interface WikiPulseFeedItem {
  relPath: string;
  title: string;
  author: string;
  authorDateUtc: string;
  sha: string;
  shortSha: string;
  subject: string;
  areaSlug: string | null;
  areaTitle: string | null;
  taskKey: string | null;
}

/** Change-feed section (recently-edited pages, newest first). */
export interface WikiPulseFeed {
  available: boolean;
  reason: string | null;
  items: WikiPulseFeedItem[];
}

/** One unfiled page plus the reason it landed in the inbox. */
export interface WikiPulseInboxItem {
  relPath: string;
  title: string;
  type: WikiNodeType;
  reason: string;
}

/** Inbox section (loose / unfiled pages; an empty list is healthy). */
export interface WikiPulseInbox {
  available: boolean;
  reason: string | null;
  count: number;
  items: WikiPulseInboxItem[];
}

/** One top-level docs folder's drift grade (worst page band + code-commit counts). */
export interface WikiPulseDriftArea {
  slug: string;
  title: string;
  grade: string; // Fresh | Aging | Stale | Empty
  pageCount: number;
  gradedPageCount: number;
  worstCommitCount: number;
  freshCount: number;
  agingCount: number;
  staleCount: number;
}

/** Roll-up of how many graded pages fall in each drift band. */
export interface WikiPulseDriftCounts {
  fresh: number;
  aging: number;
  stale: number;
  graded: number;
}

/** Drift-grading section (the per-top-folder grade bar + roll-up counts). */
export interface WikiPulseDrift {
  available: boolean;
  reason: string | null;
  overallGrade: string; // Fresh | Aging | Stale | Empty
  areas: WikiPulseDriftArea[];
  counts: WikiPulseDriftCounts;
}

/** One badly-graded page in the Pulse critical section (worst first). */
export interface WikiPulseCriticalItem {
  relPath: string;
  title: string;
  grade: string; // C | D
  assessment: string | null;
  gradedAt: string | null;
  model: string | null;
  reportPath: string | null;
  areaTitle: string | null;
}

/**
 * Critical-pages section (AGT-2051): pages a wiki-grading run scored C or D,
 * worst first, from the companion grading blocks. The LLM grade supplements the
 * deterministic drift bar. Always available (filesystem read); `overallGrade` is
 * the worst listed grade or `none`.
 */
export interface WikiPulseCritical {
  available: boolean;
  reason: string | null;
  count: number;
  overallGrade: string; // D | C | none
  items: WikiPulseCriticalItem[];
}

export interface WikiPulseWarningItem {
  kind: 'human-action' | 'dead-link';
  title: string;
  detail: string;
  humanAction: string;
  relPath: string | null;
  status: string | null;
}

export interface WikiPulseWarnings {
  available: boolean;
  reason: string | null;
  count: number;
  items: WikiPulseWarningItem[];
}

export interface WikiPulseLiveRun {
  taskKey: string;
  lane: string;
  startedAtUtc: string;
  docsFilesChanged: number;
}

export interface WikiPulseActivity {
  available: boolean;
  reason: string | null;
  runs: WikiPulseLiveRun[];
}

export type WikiLifecyclePageKind = 'design' | 'concept' | 'exploration' | 'workbench';
export type WikiLifecycleState = 'in-progress' | 'review-requested' | 'decided' | 'documented' | 'done';

export interface WikiLifecycleHistoryEntry {
  state: WikiLifecycleState | string;
  editedBy: string | null;
  editedAtUtc: string;
  note: string | null;
}

export interface WikiLifecycleItem {
  relPath: string;
  title: string;
  pageKind: WikiLifecyclePageKind | string;
  state: WikiLifecycleState | string;
  editedBy: string | null;
  editedAtUtc: string | null;
  history: WikiLifecycleHistoryEntry[];
  workbenchId: string | null;
  valid: boolean;
  error: string | null;
}

export interface WikiPulseLifecycle {
  available: boolean;
  reason: string | null;
  count: number;
  items: WikiLifecycleItem[];
}

/**
 * The generated wiki Pulse landing view: a read-only composition of the change
 * feed, the sort-needed inbox, the deterministic drift grade bar, and the LLM
 * critical-pages section. Not a wiki page - it is generated, never editable.
 * Each section carries its own `available` + `reason` so a missing source
 * degrades to an empty state.
 */
export interface WikiPulse {
  projectName: string;
  baseDir: string;
  exists: boolean;
  generatedAtUtc: string;
  feed: WikiPulseFeed;
  inbox: WikiPulseInbox;
  drift: WikiPulseDrift;
  critical: WikiPulseCritical;
  warnings?: WikiPulseWarnings;
  activity?: WikiPulseActivity;
  lifecycle?: WikiPulseLifecycle;
  workbenches?: WorkbenchCatalogue | null;
}

// ---- Wiki folder overview / search / curated home (agreed backend contracts) ----

/** Kind of a folder-overview child: a subfolder or a document page. */
export type WikiFolderChildKind = 'folder' | 'page';

/**
 * One row of a folder overview (mirrors the agreed
 * `GET /api/projects/{p}/wiki/folder/{relPath}` contract). Folders carry
 * `childCount` (and a null `fileType`); pages carry `fileType` + `size`.
 */
export interface WikiFolderChild {
  name: string;
  relPath: string;
  kind: WikiFolderChildKind;
  fileType: 'md' | 'html' | null;
  title: string;
  summary: string | null;
  updatedAt: string | null;
  /** Git author date when available; mtime is the marked fallback for pages without history. */
  updatedAtSource?: 'git' | 'mtime' | null;
  size: number | null;
  childCount: number | null;
  /** Curated classification (pages only; null for folders and unclassified pages). */
  classification?: WikiClassification | null;
  /** Observed agent reads (pages only). */
  agentReads?: WikiAgentReads | null;
}

/** Overview of one wiki folder: its path, display name, and direct children. */
export interface WikiFolderOverview {
  path: string;
  name: string;
  children: WikiFolderChild[];
}

/**
 * One search hit. `snippet` may carry `<em>` highlight markup only; everything
 * else arrives escaped and is additionally sanitised client-side before render
 * (see `sanitizeWikiSearchSnippet`).
 */
export interface WikiSearchResult {
  relPath: string;
  title: string;
  kind: string;
  snippet: string;
  score: number;
  updatedAt: string | null;
}

/** Response of `GET /api/projects/{p}/wiki/search?q=&semantic=&limit=`. */
export interface WikiSearchResponse {
  query: string;
  semanticUsed: boolean;
  expandedTerms: string[];
  durationMs: number;
  results: WikiSearchResult[];
}

/** One curated entry link; `exists=false` marks a dangling curated target. */
export interface WikiHomeLink {
  relPath: string;
  label: string;
  note: string | null;
  exists: boolean;
}

/** One curated section ("Einstiege") of the wiki home surface. */
export interface WikiHomeSection {
  title: string;
  links: WikiHomeLink[];
}

/** Response of `GET /api/projects/{p}/wiki/home` (curated landing links). */
export interface WikiHome {
  sections: WikiHomeSection[];
}

// ---- Wiki grading maintenance run (AGT-2051) ----

/** Lifecycle of a grading run (mirrors backend WikiGradingRunState, camelCased). */
export type WikiGradingRunState = 'running' | 'completed' | 'aborted' | 'failed';

/** One row in the run's recent-outcome tail. */
export interface WikiGradingRunItem {
  relPath: string;
  grade: string;
  outcome: string; // Graded | Skipped | Failed
}

/** Live status of a grading run, polled by the trigger UI. */
export interface WikiGradingRunStatus {
  projectName: string;
  runId: string;
  state: WikiGradingRunState;
  cliType: string;
  model: string;
  thinkingLevel: string | null;
  force: boolean;
  total: number;
  processed: number;
  graded: number;
  skipped: number;
  failed: number;
  critical: number;
  currentRelPath: string | null;
  startedAtUtc: string;
  completedAtUtc: string | null;
  error: string | null;
  recent: WikiGradingRunItem[];
}

/** Envelope for the status poll: `status` is null until the first run starts. */
export interface WikiGradingStatusResponse {
  status: WikiGradingRunStatus | null;
}

/** Result of an abort request. */
export interface WikiGradingAbortResponse {
  aborted: boolean;
  status: WikiGradingRunStatus | null;
}

/** Body for starting a run; every field falls back to the maintenance default. */
export interface WikiGradingRunBody {
  cliType?: string;
  model?: string;
  thinkingLevel?: string | null;
  force?: boolean;
  limit?: number;
}

/** The workspace maintenance-model default (its own CLI-management config class). */
export interface WikiMaintenanceModelConfig {
  cliType: string;
  model: string;
  thinkingLevel: string | null;
}

export interface ArchitectureDecisionSummary {
  id: string;
  title: string;
  date: string | null;
  status: string;
  body: string;
}

export interface ArchitectureOverview {
  projectName: string;
  sourceFile: string;
  exists: boolean;
  preamble: string;
  decisions: ArchitectureDecisionSummary[];
}

export interface ProjectStyleGuideAppliesTo {
  projects: string[];
  technologies: string[];
  taskAreas: string[];
}

export interface ProjectTechnology {
  key: string;
  displayLabel: string;
}

export interface ProjectStyleGuideMatch {
  projectWildcard: boolean;
  projectSelector: string;
  technologyWildcard: boolean;
  technologies: ProjectTechnology[];
}

export interface ProjectStyleGuide {
  id: string;
  title: string;
  relPath: string;
  summary: string;
  promptSummary: string;
  version: string;
  appliesTo: ProjectStyleGuideAppliesTo;
  match: ProjectStyleGuideMatch;
}

export interface ProjectStyleGuideCatalogue {
  projectKey: string;
  projectDisplayName: string;
  technologies: ProjectTechnology[];
  guides: ProjectStyleGuide[];
  warnings: { relPath: string; message: string }[];
  snapshotId: string;
  capturedAtUtc: string;
  refreshAfterUtc: string;
}
