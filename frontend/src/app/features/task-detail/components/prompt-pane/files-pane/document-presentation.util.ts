import type { StatusTone } from '../../../../../components/status-indicator/status-indicator.component';
import type { TaskArtifact } from '../../../../../models/task.model';
import { cleanStepResultMarkdown } from '../pipeline-step-result/pipeline-step-result.util';
import type { AspectDocument } from './aspect-document.model';

export type DocumentVerdictTone = 'pass' | 'concerns' | 'block' | 'neutral';

/** Status tone of a document verdict (AGT-3403); anything else stays neutral. */
const DOCUMENT_VERDICT_STATUS_TONES: Readonly<Record<DocumentVerdictTone, StatusTone>> = {
  pass: 'success',
  concerns: 'warning',
  block: 'danger',
  neutral: 'neutral',
};

export function documentVerdictStatusTone(tone: DocumentVerdictTone | null | undefined): StatusTone {
  return (tone && DOCUMENT_VERDICT_STATUS_TONES[tone]) || 'neutral';
}

export interface DocumentPresentation {
  title: string;
  verdict: string | null;
  verdictTone: DocumentVerdictTone;
  model: string | null;
  body: string;
}

/** Result documents lead the Docs tab. Source prompts and raw artifacts follow. */
export function compareDocuments(a: TaskArtifact, b: TaskArtifact): number {
  const rank = (file: TaskArtifact): number => {
    if (file.kind === 'codeReview') return 0;
    if (file.kind === 'aspect') return 1;
    if (file.kind === 'note') return 2;
    if (file.kind === 'prompt') return 3;
    return 4;
  };
  const rankDelta = rank(a) - rank(b);
  if (rankDelta !== 0) return rankDelta;
  if (a.kind === 'codeReview') return b.mtime.localeCompare(a.mtime);
  const aKey = a.aspectName || a.name;
  const bKey = b.aspectName || b.name;
  return aKey.localeCompare(bKey, undefined, { sensitivity: 'base' });
}

export function isResultDocument(file: TaskArtifact): boolean {
  return file.kind === 'codeReview' || file.kind === 'aspect' || file.kind === 'note';
}

export function documentAnchor(file: TaskArtifact): string {
  const safeName = file.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  return `doc-${safeName || 'document'}`;
}

export function presentDocument(
  file: TaskArtifact,
  raw: string | null | undefined,
  aspect: AspectDocument | null,
): DocumentPresentation {
  if (aspect) {
    return {
      title: humanize(aspect.aspect || file.aspectName || 'Review aspect'),
      verdict: verdictLabel(aspect.status),
      verdictTone: verdictTone(aspect.status),
      model: aspect.model?.trim() || file.generation?.model?.trim() || null,
      body: raw || '',
    };
  }

  const source = raw || '';
  const frontmatter = readFrontmatter(source);
  const body = cleanStepResultMarkdown(source);
  const heading = /^#{1,3}\s+(.+?)\s*$/m.exec(body)?.[1]?.replace(/[*_`]/g, '').trim();
  const rawVerdict = frontmatter['verdict'] || frontmatter['status'] || null;
  const grade = frontmatter['grade']?.toUpperCase() || null;
  const fallbackTitle = file.kind === 'codeReview'
    ? grade ? `Code review grade ${grade}` : 'Code review'
    : file.kind === 'prompt'
      ? 'Task brief'
      : file.kind === 'note'
        ? 'Review note'
        : humanize(file.name.replace(/\.[^.]+$/, ''));

  return {
    title: heading || fallbackTitle,
    verdict: verdictLabel(rawVerdict),
    verdictTone: verdictTone(rawVerdict),
    model: frontmatter['model'] || file.generation?.model?.trim() || null,
    body,
  };
}

function readFrontmatter(source: string): Record<string, string> {
  const match = /^---\s*\r?\n([\s\S]*?)\r?\n---(?:\s*\r?\n|$)/.exec(source);
  if (!match) return {};
  const values: Record<string, string> = {};
  for (const line of match[1].split(/\r?\n/)) {
    const field = /^([a-zA-Z][\w-]*):\s*(.*?)\s*$/.exec(line);
    if (!field) continue;
    values[field[1].toLowerCase()] = field[2].replace(/^['"]|['"]$/g, '').trim();
  }
  return values;
}

function verdictTone(value: string | null | undefined): DocumentVerdictTone {
  const normalized = value?.trim().toLowerCase();
  if (normalized === 'pass' || normalized === 'passed' || normalized === 'ok') return 'pass';
  if (normalized === 'concerns' || normalized === 'concern' || normalized === 'warn') return 'concerns';
  if (normalized === 'block' || normalized === 'blocked' || normalized === 'fail') return 'block';
  return 'neutral';
}

function verdictLabel(value: string | null | undefined): string | null {
  const normalized = value?.trim();
  if (!normalized) return null;
  const tone = verdictTone(normalized);
  if (tone === 'pass') return 'Pass';
  if (tone === 'concerns') return 'Concerns';
  if (tone === 'block') return 'Block';
  return humanize(normalized);
}

function humanize(value: string): string {
  const spaced = value.replace(/[-_]+/g, ' ').trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : 'Document';
}
