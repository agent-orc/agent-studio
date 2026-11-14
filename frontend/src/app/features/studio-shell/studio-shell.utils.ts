/** Canonicalise project storage paths across slash styles and case. */
export function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}

/** Match the Sidebar CLI panel to the status-bar CLI glyph colours. */
export function cliColorFor(cli: string): string {
  switch (cli) {
    case 'claude': return '#d97757';
    case 'codex': return '#569cd6';
    case 'gemini': return '#c586c0';
    default: return '#6e6e6e';
  }
}
