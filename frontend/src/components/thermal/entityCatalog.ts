import type { DataQuality, HomeAssistantEntity } from '../../types/api';

export interface EntityChoiceQuality {
  quality: DataQuality;
  reason: string;
}

/** Catalog checks are preliminary. Never infer collector health or readiness here. */
export function assessEntityChoice(
  entity: HomeAssistantEntity | null,
  expectedUnit: string,
  nowUtc: number,
  catalogIssue?: string,
  rules?: { maximumReportAgeMinutes?: number | null; minimum?: number | null; maximum?: number | null },
): EntityChoiceQuality {
  if (catalogIssue) return { quality: 'Unavailable', reason: catalogIssue };
  if (!entity) return { quality: 'Unavailable', reason: 'Den sparade entityn finns inte i den aktuella listan. Mappningen är kvar.' };
  if (entity.quality !== 'Valid' && entity.quality !== 'Stale') return {
    quality: entity.quality,
    reason: entity.qualityReason || 'Värdet kunde inte verifieras av Home Assistant-katalogen.',
  };
  if (!entity.state.trim() || /^(unknown|unavailable)$/i.test(entity.state.trim()))
    return { quality: 'Unavailable', reason: 'Home Assistant saknar ett tillgängligt värde för denna entity.' };

  const updated = timestamp(entity.lastUpdatedUtc);
  const reported = entity.lastReportedUtc == null ? updated : timestamp(entity.lastReportedUtc);
  const received = timestamp(entity.receivedAtUtc);
  const checked = timestamp(entity.checkedAtUtc);
  const validUntil = timestamp(entity.validUntilUtc);
  if (updated == null || reported == null || received == null || checked == null || validUntil == null)
    return { quality: 'Unavailable', reason: 'Aktuell värde- och enhetskontroll saknas. Hämta sensorlistan igen.' };
  if (updated > nowUtc + 30_000 || received > nowUtc + 30_000 || checked > nowUtc + 30_000 || updated > received + 30_000 ||
      reported > nowUtc + 30_000 || reported > received + 30_000 || updated > reported + 30_000)
    return { quality: 'Invalid', reason: 'Tidsstämplarna ligger i framtiden eller stämmer inte överens. Kontrollera klockorna.' };
  // Do not keep showing green indefinitely if polling pauses or the tab is resumed.
  if (nowUtc - checked > 120_000)
    return { quality: 'Stale', reason: 'Livekontrollen har inte uppdaterats på två minuter. Hämta sensorlistan igen.' };
  if (!Array.isArray(entity.compatibleUnits))
    return { quality: 'Unavailable', reason: 'Värde- och enhetskontroll saknas. Hämta sensorlistan igen.' };
  if (!entity.compatibleUnits.includes(expectedUnit)) return {
    quality: 'Invalid',
    reason: expectedUnit === 'forecast'
      ? 'Entityn saknar en läsbar temperaturprognos med minst två kommande tidpunkter. Kontrollera forecast-attributet och enheterna.'
      : `Värdet kan inte läsas som ${expectedUnit === 'bool' ? 'på/av' : expectedUnit}. Kontrollera värdet och enheten i Home Assistant.`,
  };
  const value = entity.normalizedValues?.[expectedUnit];
  if (value != null && (!Number.isFinite(value) ||
    rules?.minimum != null && value < rules.minimum || rules?.maximum != null && value > rules.maximum))
    return { quality: 'Invalid', reason: `Omräknat värde ${value} ${expectedUnit} ligger utanför tillåtet intervall ${rules?.minimum ?? '−∞'}–${rules?.maximum ?? '∞'}. Kontrollera att rätt givare valts.` };
  const reportDeadline = rules?.maximumReportAgeMinutes != null
    ? reported + rules.maximumReportAgeMinutes * 60_000 : validUntil;
  if (nowUtc - received > 600_000)
    return { quality: 'Stale', reason: 'Ingen avläsning från HA på tio minuter. Kontrollera anslutningen.' };
  if (entity.quality === 'Stale' && rules?.maximumReportAgeMinutes == null)
    return { quality: 'Stale', reason: entity.qualityReason || 'Rapportgränsen har passerats.' };
  if (nowUtc > reportDeadline)
    return { quality: 'Stale', reason: 'Rapportgränsen är passerad. Värdet kan vara oförändrat; kontrollera givarens rapportintervall. Detta är en åldersvarning, inte ett bevis på felaktigt värde.' };
  return { quality: 'Valid', reason: [entity.quality === 'Stale' ? 'Rapporten ligger inom den valda givargränsen.' : entity.qualityReason,
    'Värde, enhet och ålder är preliminärt kontrollerade. Historik och rimlighet bedöms separat.'].filter(Boolean).join(' ') };
}

function timestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
