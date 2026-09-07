/**
 * Shared compact-number formatting for the Token Economy panels (CLI usage
 * modal/detail, workspace timeline, token summary block, and the project
 * token-usage views that show the same underlying numbers). Before this
 * util each panel carried its own copy of the same K/M-suffix logic
 * without thousands separators, so a real amount like $59996.74 read as
 * one long, hard-to-parse digit run instead of $59,996.74 (AGT-2752).
 *
 * Locale-aware: separators come from the runtime locale via
 * `toLocaleString`, not a hardcoded "en-US".
 */

/**
 * "12345" -> "12K", "10940000000" -> "10,940.0M". Thousands-separated, one
 * decimal (two below 10M) above 1K. No tier above M: a lifetime workspace
 * total in the tens of billions of tokens still renders as a grouped M
 * value ("10,940.0M") rather than switching to a "B" suffix — matching
 * every existing token-count reading in the product.
 */
export function formatCompactTokens(n: number): string {
  if (!Number.isFinite(n)) return '0';
  const sign = n < 0 ? '-' : '';
  const abs = Math.abs(n);
  if (abs < 1_000) return sign + Math.trunc(abs).toString();
  if (abs < 1_000_000) return sign + fixedGrouped(abs / 1_000, abs < 10_000 ? 1 : 0) + 'K';
  return sign + fixedGrouped(abs / 1_000_000, abs < 10_000_000 ? 2 : 1) + 'M';
}

/** Two decimal places at/above $1, three below $1, four below $0.10 — thousands-separated throughout. */
export function formatCompactUsd(n: number): string {
  if (!Number.isFinite(n) || n === 0) return '$0.00';
  const sign = n < 0 ? '-' : '';
  const abs = Math.abs(n);
  if (abs < 0.1) return sign + '$' + fixedGrouped(abs, 4);
  if (abs < 1) return sign + '$' + fixedGrouped(abs, 3);
  return sign + '$' + fixedGrouped(abs, 2);
}

function fixedGrouped(value: number, digits: number): string {
  return value.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
