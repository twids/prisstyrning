/**
 * Centralized date/time formatting using the browser's locale.
 * All functions default to `navigator.language` (or the browser's built-in locale)
 * so dates render in the user's preferred format (24h vs 12h, date order, etc.).
 */

/** Full date + time, e.g. "2026-03-09 14:30" or "3/9/2026, 2:30 PM" depending on locale */
export function formatDateTime(date: Date | string | number, timeZone?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  const tz = timeZone && timeZone !== 'auto' ? timeZone : undefined;
  return d.toLocaleString(undefined, {
    timeZone: tz,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** Time only, e.g. "14:30" or "2:30 PM" */
export function formatTime(date: Date | string | number, timeZone?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  const tz = timeZone && timeZone !== 'auto' ? timeZone : undefined;
  return d.toLocaleTimeString(undefined, { timeZone: tz, hour: '2-digit', minute: '2-digit' });
}

/** Short date + full time, e.g. "9 mar 2026 14:30:00" */
export function formatDateTimeFull(date: Date | string | number, timeZone?: string): string {
  const d = date instanceof Date ? date : new Date(date);
  const tz = timeZone && timeZone !== 'auto' ? timeZone : undefined;
  return d.toLocaleString(undefined, {
    timeZone: tz,
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}
