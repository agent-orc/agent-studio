/**
 * The readable line for a failed registry request in the studio shell. Kept
 * out of studio-shell.component.ts so the component's TypeScript size stays
 * under its baseline budget, same reason as studio-shell.tab-labels.ts.
 */
export function requestErrorMessage(err: unknown): string {
  if (err && typeof err === 'object' && 'error' in err) {
    const inner = (err as { error?: unknown }).error;
    if (inner && typeof inner === 'object' && 'error' in inner)
      return String((inner as { error?: unknown }).error);
    if (typeof inner === 'string') return inner;
  }
  if (err instanceof Error) return err.message;
  return 'Request failed';
}
