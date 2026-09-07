export type UsageTokenUnit = 'K' | 'M' | 'B';

export interface UsageNumberFormatOptions {
  /** Omit to use the viewer's locale. Tests and exports may pin one. */
  locale?: string | string[];
  /**
   * Lifetime token accounting stays in millions by default so the value
   * remains directly comparable with the price catalogue's per-million rates.
   * Callers may explicitly request another unit when a fixed-scale view needs
   * it.
   */
  unit?: UsageTokenUnit;
}

const TOKEN_DIVISORS: Record<UsageTokenUnit, number> = {
  K: 1_000,
  M: 1_000_000,
  B: 1_000_000_000,
};

const currencyFormatters = new Map<string, Intl.NumberFormat>();
const decimalFormatters = new Map<string, Intl.NumberFormat>();

/** Locale-aware USD used by token usage tiles, tables, and detail views. */
export function formatUsageCurrency(
  value: number | null | undefined,
  options: UsageNumberFormatOptions = {},
): string {
  const amount = Number.isFinite(value) ? value! : 0;
  const key = localeKey(options.locale);
  let formatter = currencyFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(options.locale, {
      style: 'currency',
      currency: 'USD',
      currencyDisplay: 'narrowSymbol',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    });
    currencyFormatters.set(key, formatter);
  }
  return formatter.format(amount);
}

/**
 * Locale-aware token count with one decimal for compact K/M/B values.
 * The default scale tops out at M by product convention, which keeps a
 * lifetime value such as 10,940,000,000 readable as `10,940.0M`.
 */
export function formatUsageTokens(
  value: number | null | undefined,
  options: UsageNumberFormatOptions = {},
): string {
  const tokens = Number.isFinite(value) ? value! : 0;
  const absolute = Math.abs(tokens);
  const unit = options.unit ?? (absolute < 1_000_000 ? 'K' : 'M');
  const scaled = tokens / TOKEN_DIVISORS[unit];
  const key = localeKey(options.locale);
  let formatter = decimalFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(options.locale, {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1,
      useGrouping: true,
    });
    decimalFormatters.set(key, formatter);
  }
  const formatted = formatter.format(scaled);
  return `${formatted}${unit}`;
}

function localeKey(locale: string | string[] | undefined): string {
  return locale === undefined ? 'viewer-default' : JSON.stringify(locale);
}
