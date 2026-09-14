import type {
  ArticlePattern,
  WorkbenchListItem,
  WorkbenchOverviewItem,
  WorkbenchStatus,
} from '../models/project-docs.model';

/**
 * One resolver for every way a Dossier is named in prose (AGT-2812).
 *
 * Wherever the product writes about a Dossier it uses one of three forms: the
 * project-scoped reference key (`AGT-W54`), a repo-relative path to the Dossier
 * folder or its HTML entry point, or the path of the folder's
 * `workbench.json` descriptor. All three resolve here against the Dossier
 * catalogue; anything that does not resolve stays plain text, because a chip on
 * a path that is not a Dossier would be worse than no chip at all.
 */
export interface DossierReference {
  /** Descriptor folder id. This is the Dossier route segment. */
  id: string;
  key: string | null;
  title: string;
  status: WorkbenchStatus;
  phase: string | null;
  pattern: ArticlePattern | null;
  valid: boolean;
  projectName: string;
  /** Repo-relative entry point. The chip's secondary "open source" target. */
  entryPath: string;
}

export interface DossierIndex {
  readonly byKey: ReadonlyMap<string, readonly DossierReference[]>;
  readonly byEntryPath: ReadonlyMap<string, readonly DossierReference[]>;
  readonly byDirectory: ReadonlyMap<string, readonly DossierReference[]>;
  readonly size: number;
}

export interface DossierReferenceCandidate {
  start: number;
  end: number;
  token: string;
}

export const EMPTY_DOSSIER_INDEX: DossierIndex = {
  byKey: new Map(), byEntryPath: new Map(), byDirectory: new Map(), size: 0,
};

/** `PROJECT-W<number>`, the canonical Dossier reference key. */
const KEY_PATTERN = /(^|[^A-Za-z0-9_-])([A-Za-z][A-Za-z0-9]{1,5}-W\d+)(?=$|[^A-Za-z0-9_-])/gi;
const KEY_ONLY_PATTERN = /^[A-Za-z][A-Za-z0-9]{1,5}-W\d+$/i;
/** A repo-relative path token: at least one segment separator, no protocol. */
const PATH_PATTERN = /(^|[\s("'`[<])((?:\.\/)?(?:[A-Za-z0-9._-]+\/)+[A-Za-z0-9._-]*)/g;
const TRAILING_PUNCTUATION = /[.,;:!?)\]}'"`]+$/;
const DESCRIPTOR_FILE = 'workbench.json';

export function dossierReferenceFromItem(
  projectName: string,
  item: WorkbenchListItem,
): DossierReference {
  return {
    id: item.id,
    key: item.key ?? null,
    title: item.title,
    status: item.status,
    phase: item.phase ?? null,
    pattern: item.pattern ?? null,
    valid: item.valid,
    projectName,
    entryPath: item.entryPath,
  };
}

export function buildDossierIndex(
  items: readonly WorkbenchOverviewItem[] | readonly DossierReference[],
): DossierIndex {
  const references = items.map(item =>
    'workbench' in item ? dossierReferenceFromItem(item.projectName, item.workbench) : item);
  const byKey = new Map<string, DossierReference[]>();
  const byEntryPath = new Map<string, DossierReference[]>();
  const byDirectory = new Map<string, DossierReference[]>();
  for (const reference of references) {
    if (reference.key) push(byKey, reference.key.toUpperCase(), reference);
    const entryPath = normalizePath(reference.entryPath);
    if (!entryPath) continue;
    push(byEntryPath, entryPath, reference);
    const directory = parentDirectory(entryPath);
    if (directory) push(byDirectory, directory, reference);
  }
  return { byKey, byEntryPath, byDirectory, size: references.length };
}

/**
 * Every Dossier-shaped token in one run of text, in reading order and without
 * overlaps. Resolution happens separately so an unknown token costs nothing.
 */
export function dossierReferenceCandidates(text: string): DossierReferenceCandidate[] {
  const result: DossierReferenceCandidate[] = [];
  // Paths first: a Dossier folder can be named after its own key
  // (`docs/operations/agt-w54/`), and the path is the more specific reading.
  collect(text, PATH_PATTERN, result);
  collect(text, KEY_PATTERN, result);
  return result.sort((left, right) => left.start - right.start);
}

/**
 * Resolve one token against the catalogue. `projectHint` decides between two
 * projects that ship the same repo-relative path; without it an ambiguous token
 * stays text rather than guessing a project.
 */
export function resolveDossierReference(
  token: string,
  index: DossierIndex,
  projectHint: string | null = null,
): DossierReference | null {
  const trimmed = token.trim().replace(TRAILING_PUNCTUATION, '');
  if (!trimmed) return null;
  if (KEY_ONLY_PATTERN.test(trimmed)) {
    return pick(index.byKey.get(trimmed.toUpperCase()), projectHint);
  }
  const path = normalizePath(trimmed);
  if (!path) return null;
  if (path.endsWith(`/${DESCRIPTOR_FILE}`)) {
    return pick(index.byDirectory.get(path.slice(0, -(DESCRIPTOR_FILE.length + 1))), projectHint);
  }
  return pick(index.byEntryPath.get(path), projectHint)
    ?? pick(index.byDirectory.get(path), projectHint);
}

/** The Dossier route the list uses, so a chip and a list row land on one view. */
export function dossierRoute(projectId: string, dossierId: string): string {
  return `#/projects/${encodeURIComponent(projectId.trim().toUpperCase())}`
    + `/workbenches/${encodeURIComponent(dossierId)}`;
}

function collect(
  text: string,
  pattern: RegExp,
  result: DossierReferenceCandidate[],
): void {
  pattern.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(text))) {
    const start = match.index + (match[1]?.length ?? 0);
    const raw = match[2].replace(TRAILING_PUNCTUATION, '');
    if (!raw) continue;
    const candidate = { start, end: start + raw.length, token: raw };
    if (result.some(other => candidate.start < other.end && other.start < candidate.end)) continue;
    result.push(candidate);
  }
}

function push(map: Map<string, DossierReference[]>, key: string, value: DossierReference): void {
  const bucket = map.get(key);
  if (bucket) bucket.push(value);
  else map.set(key, [value]);
}

function pick(
  bucket: readonly DossierReference[] | undefined,
  projectHint: string | null,
): DossierReference | null {
  if (!bucket?.length) return null;
  const hinted = projectHint
    ? bucket.filter(item => item.projectName.toLowerCase() === projectHint.toLowerCase())
    : [];
  if (hinted.length === 1) return hinted[0];
  return bucket.length === 1 ? bucket[0] : null;
}

/** Lower-cased, separator-normalised, without `./` prefix or trailing slash. */
function normalizePath(value: string): string {
  return value
    .trim()
    .replaceAll('\\', '/')
    .replace(/^\.\//, '')
    .replace(/^\/+/, '')
    .replace(/\/+$/, '')
    .toLowerCase();
}

function parentDirectory(path: string): string {
  const index = path.lastIndexOf('/');
  return index > 0 ? path.slice(0, index) : '';
}
