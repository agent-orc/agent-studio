export function wikiSafeViewerTab(value: unknown): 'doc' | 'report' | 'source' | 'edit' {
  return value === 'source' || value === 'doc' || value === 'report' || value === 'edit'
    ? value
    : 'doc';
}

export function wikiSafeReportAnchor(anchor: string | null): string | null {
  if (!anchor) return null;
  const clean = anchor.trim().toLowerCase();
  return /^[a-z0-9-]+$/.test(clean) ? clean : null;
}

export function wikiReadStoredExpandedIds(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0);
}
