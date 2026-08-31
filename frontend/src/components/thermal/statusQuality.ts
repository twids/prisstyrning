import type { DataQuality, ThermalStatus } from '../../types/api';

export function describeStatusQuality(status: ThermalStatus, now: number): { quality: DataQuality; reason: string } {
  const fallbackReasons: Record<DataQuality, string> = {
    Valid: 'Aktiverade datakällor är giltiga i senaste insamlingen.',
    Invalid: 'Minst en aktiverad datakälla är ogiltig eller exkluderad.',
    Stale: 'Insamlingen eller en aktiverad givares mätning är för gammal.',
    Unavailable: 'Aktuell kvalitet för alla aktiverade datakällor kan inte bekräftas.',
  };
  // Never keep a cached green badge when polling is paused or a tab was asleep.
  // Server-reported faults are never upgraded by this presentation safeguard.
  if (status.overallDataQuality === 'Valid') {
    if (!status.lastTelemetryUtc) return { quality: 'Unavailable', reason: 'Ingen sparad femminutersinsamling finns.' };
    const age = now - Date.parse(status.lastTelemetryUtc);
    if (!Number.isFinite(age) || age < 0) return { quality: 'Invalid', reason: 'Insamlingens tidsstämpel är ogiltig eller ligger i framtiden. Kontrollera klockorna.' };
    if (age > 10 * 60_000) return { quality: 'Stale', reason: 'Senaste sparade insamlingen är äldre än tio minuter. Aktuell datakvalitet kan inte bekräftas.' };
  }
  return { quality: status.overallDataQuality, reason: status.dataQualityReason || fallbackReasons[status.overallDataQuality] };
}
