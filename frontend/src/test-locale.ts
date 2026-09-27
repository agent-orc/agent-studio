/**
 * Pins the default locale of the unit-test process to `en-US`.
 *
 * Node takes the operating-system locale as the default for `Intl` and for the
 * `toLocale*` helpers, and on Windows neither `LANG` nor `LC_ALL` override it.
 * A `de-DE` workstation otherwise renders `1.000` where specs expect
 * `1,000`. Product code uses explicit locales; this test-only fallback also
 * makes dependency and spec code deterministic on the operator workstation.
 *
 * Calls that pass an explicit locale are left untouched.
 */
const DEFAULT_LOCALE = 'en-US';

type LocaleArg = string | string[] | undefined;

const withDefaultLocale = (locales: LocaleArg): LocaleArg =>
  locales === undefined ? DEFAULT_LOCALE : locales;

const numberToLocaleString = Number.prototype.toLocaleString;
Number.prototype.toLocaleString = function (this: number, locales?: LocaleArg, options?: Intl.NumberFormatOptions) {
  return numberToLocaleString.call(this, withDefaultLocale(locales), options);
};

const dateToLocaleString = Date.prototype.toLocaleString;
Date.prototype.toLocaleString = function (this: Date, locales?: LocaleArg, options?: Intl.DateTimeFormatOptions) {
  return dateToLocaleString.call(this, withDefaultLocale(locales), options);
};

const dateToLocaleDateString = Date.prototype.toLocaleDateString;
Date.prototype.toLocaleDateString = function (this: Date, locales?: LocaleArg, options?: Intl.DateTimeFormatOptions) {
  return dateToLocaleDateString.call(this, withDefaultLocale(locales), options);
};

const dateToLocaleTimeString = Date.prototype.toLocaleTimeString;
Date.prototype.toLocaleTimeString = function (this: Date, locales?: LocaleArg, options?: Intl.DateTimeFormatOptions) {
  return dateToLocaleTimeString.call(this, withDefaultLocale(locales), options);
};

/** Wraps an `Intl` constructor so a missing locale argument becomes the default locale. */
function pinDefaultLocale<T extends object>(original: T): T {
  return new Proxy(original, {
    construct(target, args: unknown[], newTarget) {
      const patched = args.length === 0 ? [DEFAULT_LOCALE] : [withDefaultLocale(args[0] as LocaleArg), ...args.slice(1)];
      return Reflect.construct(target as unknown as new (...a: unknown[]) => object, patched, newTarget);
    },
    apply(target, thisArg, args: unknown[]) {
      const patched = args.length === 0 ? [DEFAULT_LOCALE] : [withDefaultLocale(args[0] as LocaleArg), ...args.slice(1)];
      return Reflect.apply(target as unknown as (...a: unknown[]) => unknown, thisArg, patched);
    },
  });
}

for (const name of ['NumberFormat', 'DateTimeFormat', 'RelativeTimeFormat', 'ListFormat', 'PluralRules', 'Collator'] as const) {
  const original = (Intl as unknown as Record<string, object | undefined>)[name];
  if (original) {
    Object.defineProperty(Intl, name, { value: pinDefaultLocale(original), configurable: true, writable: true });
  }
}
