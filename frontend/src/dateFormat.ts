/**
 * Centralized date/time formatting using the browser's locale.
 * All functions default to `navigator.language` (or the browser's built-in locale)
 * so dates render in the user's preferred format (24h vs 12h, date order, etc.).
 */

function resolveTimeZone(timeZone?: string): string | undefined {
  return timeZone && timeZone !== 'auto' ? timeZone : undefined;
}

/** Locales that conventionally use 24-hour time. */
const HOUR24_LOCALES = [
  'sv-SE',
  'nb-NO',
  'no-NO',
  'nn-NO',
  'da-DK',
  'fi-FI',
  'sv',
  'nb',
  'no',
  'nn',
  'da',
  'fi',
];
function resolveLocale(locale?: string): string | undefined {
  return locale && locale !== 'auto' ? locale : undefined;
}

/** Returns hourCycle override when the locale uses 24h time, to prevent OS-level 12h overrides. */
function resolveHourCycle(locale?: string): { hourCycle: 'h23' } | {} {
  const resolved = resolveLocale(locale);
  if (!resolved) {
    return {};
  }

  const primaryLanguage = resolved.split('-')[0];

  const uses24h = HOUR24_LOCALES.some(l => {
    // Full locale tag (e.g. "sv-SE") – match exact tag or that tag with extra subtags.
    if (l.includes('-')) {
      return resolved === l || resolved.startsWith(`${l}-`);
    }
    // Language-only tag (e.g. "sv", "fi") – match primary language subtag exactly.
    return primaryLanguage === l;
  });

  if (uses24h) {
    return { hourCycle: 'h23' as const };
  }

  return {};
}

/** Full date + time, e.g. "2026-03-09 14:30" or "3/9/2026, 2:30 PM" depending on locale */
export function formatDateTime(date: Date | string | number, timeZone?: string, locale?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleString(resolveLocale(locale), {
    timeZone: resolveTimeZone(timeZone),
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    ...resolveHourCycle(locale),
  });
}

/** Time only, e.g. "14:30" or "2:30 PM" */
export function formatTime(date: Date | string | number, timeZone?: string, locale?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleTimeString(resolveLocale(locale), {
    timeZone: resolveTimeZone(timeZone),
    hour: '2-digit',
    minute: '2-digit',
    ...resolveHourCycle(locale),
  });
}

/** Short date + full time, e.g. "9 mar 2026 14:30:00" */
export function formatDateTimeFull(date: Date | string | number, timeZone?: string, locale?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleString(resolveLocale(locale), {
    timeZone: resolveTimeZone(timeZone),
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    ...resolveHourCycle(locale),
  });
}
