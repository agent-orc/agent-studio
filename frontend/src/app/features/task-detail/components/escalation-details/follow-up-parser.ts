export interface FollowUpField {
  label: string;
  value: string;
  display: string;
  kind: 'text' | 'path' | 'ref' | 'sha';
}

export interface FollowUpItem {
  id: string;
  checked: boolean;
  kind: string;
  fields: FollowUpField[];
  cause: string | null;
  rawOutput: string | null;
}

const LABELS: Record<string, string> = {
  host: 'Host', worktree: 'Worktree', branch: 'Branch', canonical: 'Canonical ref',
  'retained local HEAD': 'Retained local HEAD', failure: 'Failure',
};

function field(label: string, value: string): FollowUpField {
  const normalized = value.replace(/^refs\/heads\//, '');
  const kind = /^[a-f0-9]{40,64}$/i.test(normalized) ? 'sha'
    : label === 'worktree' ? 'path'
    : label === 'branch' || label === 'canonical' || label === 'ref' ? 'ref' : 'text';
  const display = kind === 'sha' ? normalized.slice(0, 9)
    : (kind === 'path' || kind === 'ref') && normalized.length > 72
      ? `${normalized.slice(0, 38)}…${normalized.slice(-28)}` : normalized;
  return { label: LABELS[label] ?? label.replace(/\b\w/g, c => c.toUpperCase()), value: normalized, display, kind };
}

function readableOutput(source: string): string {
  return source
    .replace(/\s{7,}(?=remote:|error:|! \[remote rejected\])/g, '\n')
    .split('\n')
    .map(line => line.trim().replace(/^(?:remote:\s*)+/i, '').trim())
    .filter(line => line && !/^[—─\s]+$/.test(line))
    .join('\n');
}

function causeOf(raw: string): string | null {
  if (/GITHUB PUSH PROTECTION/i.test(raw) && /Push cannot contain secrets/i.test(raw)) {
    const secret = raw.match(/[—─]\s*([^—─]+?)\s*[—─]{3,}/)?.[1]?.trim();
    const path = raw.match(/path:\s*([^\s]+:\d+)/)?.[1];
    return `GitHub push protection: secret detected${secret ? ` (${secret})` : ''}${path ? ` at ${path}` : ''}`;
  }
  if (/permission denied/i.test(raw)) return 'Push failed: permission denied';
  if (/repository not found/i.test(raw)) return 'Push failed: repository not found';
  return null;
}

/** Parse known checklist items; unknown prose stays in the document source. */
export function parseFollowUp(markdown: string): FollowUpItem[] {
  return markdown.split(/\r?\n/).flatMap((line, index) => {
    const match = line.match(/^\s*- \[([ xX])\] ([\w-]+):\s*(.+)$/);
    if (!match) return [];
    const [, mark, kind, body] = match;
    if (!/[A-Za-z][\w -]*=/.test(body)) return [];
    const rawMatch = body.match(/(?:^|; )failure=(.*)$/);
    const beforeFailure = rawMatch ? body.slice(0, rawMatch.index).replace(/;\s*$/, '') : body;
    const fields = beforeFailure.split(/;\s*/).flatMap(part => {
      const pair = part.match(/^([^=]+)=(.*)$/);
      if (pair) return [field(pair[1].trim(), pair[2].trim())];
      const canonical = part.match(/^canonical\s+(refs\/heads\/\S+)\s+at\s+(\S+)$/);
      if (canonical) return [field('canonical', canonical[1]), field('remote state', canonical[2])];
      const head = part.match(/^retained local HEAD\s+([a-f0-9]{40,64})$/i);
      return head ? [field('retained local HEAD', head[1])] : [];
    });
    const raw = rawMatch?.[1] ?? null;
    return [{ id: `${kind}-${index}`, checked: mark.toLowerCase() === 'x', kind,
      fields, cause: raw ? causeOf(raw) : null, rawOutput: raw ? readableOutput(raw) : null }];
  });
}
