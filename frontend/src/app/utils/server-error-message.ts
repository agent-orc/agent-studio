/**
 * The server's own words for a refused request (AGT-3582). A refusal toast
 * shows them instead of a generic line, so the operator reads why the server
 * said no.
 *
 * Bodies come in two shapes: `{ error: "<sentence>" }`, and
 * `{ error: "<code>", message: "<sentence>" }` (for example
 * `batch-isolation-active`). A code-shaped `error` (no whitespace) gives way
 * to `message`; otherwise `error`, `message`, then `detail` win in that order.
 */
export function serverErrorMessage(error: unknown, fallback: string): string {
  if (typeof error !== 'object' || error === null) return fallback;
  const body = (error as { error?: unknown }).error;
  if (typeof body === 'string' && body.trim()) return body.trim();
  if (typeof body === 'object' && body !== null) {
    const record = body as Record<string, unknown>;
    const code = text(record['error']);
    const message = text(record['message']);
    if (code && message && !/\s/.test(code)) return message;
    const found = code ?? message ?? text(record['detail']);
    if (found) return found;
  }
  return fallback;
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value.trim() : null;
}
