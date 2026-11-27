import type { StudioIconName } from '@studio/ui/components/studio-icon/studio-icon.component';
import type { WikiClassification } from './project-docs.model';

/** Canonical interactive page kinds shared by Wiki, Dossier, and chat. */
export type PageType = 'doc' | 'concept' | 'workbench' | 'incident' | 'report';

/** One repository page in the operator's current navigation scope. */
export interface PageContext {
  projectName: string;
  relPath: string;
  title: string;
  pageType: PageType;
  excerpt: string;
}

export type PageTaskIntent = 'create-task' | 'build-feature' | 'create-follow-up';

export interface PageTaskRequest {
  context: PageContext;
  intent: PageTaskIntent;
}

export function pageContextKey(context: PageContext): string {
  return `page:${context.projectName}/${context.relPath}`;
}

/** Derive the canonical page type from companion meta, registration, and path. */
export function derivePageType(
  relPath: string,
  classification?: WikiClassification | null,
  registeredWorkbenchPaths: ReadonlySet<string> = new Set<string>(),
): PageType {
  const rel = normalizeRelPath(relPath);
  if (registeredWorkbenchPaths.has(rel) || registeredWorkbenchPaths.has(`docs/${rel}`)) return 'workbench';

  const canonical = classification?.pageType?.trim().toLowerCase();
  if (canonical === 'doc' || canonical === 'concept' || canonical === 'workbench'
    || canonical === 'incident' || canonical === 'report') {
    return canonical;
  }

  const curated = classification?.type?.trim().toLowerCase();
  if (curated === 'workbench') return 'workbench';
  if (curated === 'konzept' || curated === 'concept') return 'concept';
  if (curated === 'incident' || curated === 'history' || curated === 'incident/history') return 'incident';
  if (curated === 'report' || curated === 'analyse' || curated === 'analysis' || curated === 'generiert') {
    return 'report';
  }

  if (/(^|\/)(incident|incidents|history|historie)(\/|[.-])/i.test(rel)) return 'incident';
  if (/(^|\/)(report|reports)(\/|[.-])|\.report\./i.test(rel)) return 'report';
  if (/(^|\/)(workbench|workbenches)(\/|[.-])/i.test(rel)) return 'workbench';
  if (/(^|\/)(concept|concepts)(\/|[.-])/i.test(rel)) return 'concept';
  return 'doc';
}

export function pageTypeLabel(type: PageType): string {
  switch (type) {
    case 'concept': return 'Concept';
    case 'workbench': return 'Dossier';
    case 'incident': return 'Incident / history';
    case 'report': return 'Report';
    default: return 'Document';
  }
}

export function pageTypeIcon(type: PageType): StudioIconName {
  switch (type) {
    case 'concept': return 'book';
    case 'workbench': return 'eye';
    case 'incident': return 'activity';
    case 'report': return 'list';
    default: return 'file';
  }
}

/** Compact, plain-text excerpt safe for task prompts and navigation context. */
export function pageExcerpt(content: string, fallback: string, limit = 480): string {
  const text = (content ?? '')
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, ' ')
    .replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/[#>*_`[\]()~-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  const resolved = text || fallback.trim();
  return resolved.length <= limit ? resolved : `${resolved.slice(0, limit - 1).trimEnd()}…`;
}

function normalizeRelPath(relPath: string): string {
  return relPath.replaceAll('\\', '/').replace(/^\/+/, '').replace(/^docs\//i, '');
}
