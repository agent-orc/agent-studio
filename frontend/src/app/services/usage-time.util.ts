/**
 * The one date and time format of the usage, quota popover and model-selection
 * surfaces (HUC-05, AGT-3794-F14): `Sun 15 Nov, 22:34 Europe/Berlin`, always
 * with an explicit zone, followed by the UTC equivalent when the zone is not
 * UTC. Aliases of UTC such as `Etc/UTC` read as `UTC`.
 */

const UTC_ALIASES = new Set(['utc', 'etc/utc', 'etc/uct', 'uct', 'etc/zulu', 'zulu', 'z', 'gmt', 'etc/gmt',
  'etc/gmt0', 'etc/gmt+0', 'etc/gmt-0', 'etc/greenwich', 'etc/universal', 'universal']);

/** A valid IANA zone, `UTC` for a missing, invalid or UTC-alias zone. */
export function usageTimeZone(timeZone: string | null | undefined): string {
  const zone = timeZone?.trim();
  if (!zone || UTC_ALIASES.has(zone.toLowerCase())) return 'UTC';
  try {
    new Intl.DateTimeFormat('en-GB', { timeZone: zone });
    return zone;
  } catch {
    return 'UTC';
  }
}

/** The zone of this browser, for surfaces that have no workspace zone. */
export function browserTimeZone(): string {
  try {
    return usageTimeZone(new Intl.DateTimeFormat('en-GB').resolvedOptions().timeZone);
  } catch {
    return 'UTC';
  }
}

/** `Sun 15 Nov, 22:34` in `timeZone`, without the zone name. */
export function formatInZone(date: Date, timeZone: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone, weekday: 'short', day: 'numeric', month: 'short',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).format(date);
}

function parse(value: string | Date | null | undefined): Date | null {
  if (value == null || value === '') return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** `Sun 15 Nov, 22:34 Europe/Berlin`; `null` for missing or invalid input. */
export function formatZonedTime(value: string | Date | null | undefined, timeZone: string | null | undefined): string | null {
  const date = parse(value);
  if (!date) return null;
  const zone = usageTimeZone(timeZone);
  return `${formatInZone(date, zone)} ${zone}`;
}

/**
 * Zone-local time with its UTC equivalent:
 * `Mon 29 Sept, 09:00 Europe/Berlin (Mon 29 Sept, 07:00 UTC)`, or only
 * `Mon 29 Sept, 07:00 UTC` when the zone is UTC.
 */
export function formatZonedWithUtc(value: string | Date | null | undefined, timeZone: string | null | undefined): string | null {
  const date = parse(value);
  if (!date) return null;
  const zone = usageTimeZone(timeZone);
  const local = `${formatInZone(date, zone)} ${zone}`;
  return zone === 'UTC' ? local : `${local} (${formatInZone(date, 'UTC')} UTC)`;
}
