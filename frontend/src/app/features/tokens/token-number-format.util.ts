export type TokenCountUnit = 'K' | 'M' | 'B';

export interface TokenCountFormatOptions {
  /** Defaults to the viewer's browser locale. */
  locale?: Intl.LocalesArgument;
  /** Prevents a compact value from scaling past this unit. */
  maximumUnit?: TokenCountUnit;
}

const TOKEN_UNITS: readonly { suffix: TokenCountUnit; divisor: number }[] = [
  { suffix: 'K', divisor: 1_000 },
  { suffix: 'M', divisor: 1_000_000 },
  { suffix: 'B', divisor: 1_000_000_000 },
];

/** Locale-aware exact token count for formulas and detailed counters. */
export function formatTokenExactCount(
  value: number | null | undefined,
  locale?: Intl.LocalesArgument,
): string {
  const finiteValue = Number.isFinite(value) ? Number(value) : 0;
  return new Intl.NumberFormat(locale, {
    maximumFractionDigits: 0,
    useGrouping: true,
  }).format(finiteValue);
}

/** Locale-aware compact token count with one decimal for K/M/B values. */
export function formatTokenCount(
  value: number | null | undefined,
  options: TokenCountFormatOptions = {},
): string {
  const finiteValue = Number.isFinite(value) ? Number(value) : 0;
  const maximumUnit = options.maximumUnit ?? 'B';
  const maximumIndex = TOKEN_UNITS.findIndex(unit => unit.suffix === maximumUnit);
  const absolute = Math.abs(finiteValue);

  let selected = -1;
  for (let index = 0; index <= maximumIndex; index++) {
    if (absolute >= TOKEN_UNITS[index].divisor) selected = index;
  }

  if (selected < 0) {
    return formatTokenExactCount(finiteValue, options.locale);
  }

  const unit = TOKEN_UNITS[selected];
  const number = new Intl.NumberFormat(options.locale, {
    minimumFractionDigits: 1,
    maximumFractionDigits: 1,
    useGrouping: true,
  }).format(finiteValue / unit.divisor);
  return `${number}${unit.suffix}`;
}

/** Locale-aware USD display used by token-economy totals and table cells. */
export function formatTokenCurrencyUsd(
  value: number | null | undefined,
  locale?: Intl.LocalesArgument,
): string {
  const finiteValue = Number.isFinite(value) ? Number(value) : 0;
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency: 'USD',
    currencyDisplay: 'symbol',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
    useGrouping: true,
  }).format(finiteValue);
}

/** Known-price subtotal plus an explicit count of models without a price. */
export function formatTokenCostTotal(
  knownCostUsd: number | null | undefined,
  unpricedModels: number,
  locale?: Intl.LocalesArgument,
): string {
  const subtotal = formatTokenCurrencyUsd(knownCostUsd, locale);
  const missing = Math.max(0, Math.trunc(unpricedModels));
  return missing > 0 ? `${subtotal} + ${missing} unpriced` : subtotal;
}
