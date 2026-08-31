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
): EntityChoiceQuality {
  if (catalogIssue) return { quality: 'Unavailable', reason: catalogIssue };
  if (!entity) return { quality: 'Unavailable', reason: 'Den sparade entityn finns inte i den aktuella listan. Mappningen är kvar.' };
  if (entity.quality !== 'Valid') return {
    quality: entity.quality,
    reason: entity.qualityReason || 'Värdet kunde inte verifieras av Home Assistant-katalogen.',
  };
  if (!entity.state.trim() || /^(unknown|unavailable)$/i.test(entity.state.trim()))
    return { quality: 'Unavailable', reason: 'Home Assistant saknar ett tillgängligt värde för denna entity.' };

  const updated = timestamp(entity.lastUpdatedUtc);
  const received = timestamp(entity.receivedAtUtc);
  const checked = timestamp(entity.checkedAtUtc);
  const validUntil = timestamp(entity.validUntilUtc);
  if (updated == null || received == null || checked == null || validUntil == null)
    return { quality: 'Unavailable', reason: 'Aktuell värde- och enhetskontroll saknas. Hämta sensorlistan igen.' };
  if (updated > nowUtc + 30_000 || received > nowUtc + 30_000 || checked > nowUtc + 30_000 || updated > received + 30_000)
    return { quality: 'Invalid', reason: 'Tidsstämplarna ligger i framtiden eller stämmer inte överens. Kontrollera klockorna.' };
  if (nowUtc > validUntil)
    return { quality: 'Stale', reason: 'Värdet har passerat kontots åldersgräns. Väntar på ett färskt värde.' };
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
  return { quality: 'Valid', reason: 'Värde, enhet och ålder är preliminärt kontrollerade. Historik och rimlighet bedöms separat.' };
}

function timestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
