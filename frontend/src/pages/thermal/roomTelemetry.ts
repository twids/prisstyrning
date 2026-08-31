import type { DataQuality, ThermalRoomConfig, ThermalTelemetrySample } from '../../types/api';

type RoomSnapshot = Pick<ThermalTelemetrySample, 'timestampUtc' | 'roomTemperaturesJson' | 'qualityJson'>;
export type RoomReadingStatus = DataQuality | 'Excluded' | 'Unknown' | 'Imported' | 'Disabled' | 'FetchError';
export interface RoomReading {
  status: RoomReadingStatus;
  value: number | null;
  kind: 'measurement' | 'fallback' | 'none';
  current: boolean;
  detail: string;
}

const emptyReading = { value: null, kind: 'none' as const, current: false };
const maximumAgeMs = 10 * 60_000;

/** Presentation only. Never infer a valid sensor from a non-null control fallback. */
export function describeRoomReading(
  room: ThermalRoomConfig,
  sample: RoomSnapshot | undefined,
  now: number,
  fetchFailed = false,
): RoomReading {
  if (!room.enabled) return { ...emptyReading, status: 'Disabled', detail: 'Rummet ingår inte i styrningen.' };
  if (fetchFailed) return { ...emptyReading, status: 'FetchError', detail: 'Aktuella mätvärden och komfort kan inte bekräftas.' };
  if (!sample) return { ...emptyReading, status: 'Unavailable', detail: 'Ingen rumsmätning har hämtats ännu.' };

  const age = now - Date.parse(sample.timestampUtc);
  if (!Number.isFinite(age) || age < 0) {
    return { ...emptyReading, status: 'Unknown', detail: 'Tidsstämpeln är ogiltig eller ligger i framtiden. Kontrollera klockorna.' };
  }
  const metadata = parseRecord(sample.qualityJson);
  const assessment = property(property(metadata, 'rooms'), room.entityId);
  const quality = readQuality(property(assessment, 'quality'));
  const excluded = property(assessment, 'excluded');
  if (quality === null || typeof excluded !== 'boolean') {
    return { ...emptyReading, status: 'Unknown', detail: 'Kvalitetsstatus saknas eller kan inte tolkas. Värdet räknas inte som en giltig mätning.' };
  }

  const raw = property(parseRecord(sample.roomTemperaturesJson), room.entityId);
  const value = typeof raw === 'number' && Number.isFinite(raw) && raw >= room.minimumValidC && raw <= room.maximumValidC ? raw : null;
  const measured = quality === 'Valid' && !excluded;
  const kind = value === null ? 'none' : measured ? 'measurement' : room.isCritical ? 'fallback' : 'none';
  const saved = { value: kind === 'none' ? null : value, kind, current: false } as const;

  if (property(metadata, 'source') === 'HomeAssistantHistoryImport') {
    return { ...saved, status: 'Imported', detail: 'Importerad historik bekräftar inte rummets aktuella temperatur eller komfort.' };
  }
  if (age > maximumAgeMs) {
    return { ...saved, status: 'Stale', detail: 'Insamlade uppgifter är äldre än tio minuter. Aktuell sensorkvalitet och komfort är okända.' };
  }
  if (excluded) {
    return { ...saved, status: 'Excluded', detail: 'Givaren inväntar tre giltiga mätningar innan den används igen.' };
  }
  if (quality === 'Stale') {
    return { ...saved, status: 'Stale', detail: 'Givarens mätning är för gammal även om uppgifterna samlades in nyligen.' };
  }
  if (quality === 'Invalid') {
    return { ...saved, status: 'Invalid', detail: 'Kontrollera givarens enhet, tillåtna intervall och förändringstakt.' };
  }
  if (quality === 'Unavailable') {
    return { ...saved, status: 'Unavailable', detail: 'Givaren saknar ett tillgängligt aktuellt mätvärde.' };
  }
  if (value === null) {
    return { ...emptyReading, status: 'Invalid', detail: 'Mätvärdet saknas, är felaktigt eller ligger utanför rummets tillåtna intervall.' };
  }
  return { value, kind: 'measurement', current: true, status: 'Valid', detail: 'Mätningen är godkänd och senaste insamlingen är högst tio minuter gammal.' };
}

function parseRecord(json: string): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(json);
    return isRecord(value) ? value : {};
  } catch { return {}; }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function property(value: unknown, name: string): unknown {
  if (!isRecord(value)) return undefined;
  // Live snapshots have PascalCase fields, imported snapshots use camelCase.
  return Object.entries(value).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1];
}

function readQuality(value: unknown): DataQuality | null {
  const qualities: DataQuality[] = ['Valid', 'Stale', 'Invalid', 'Unavailable'];
  if (typeof value === 'number' && Number.isInteger(value)) return qualities[value] ?? null;
  if (typeof value === 'string') return qualities.find(quality => quality.toLowerCase() === value.toLowerCase()) ?? null;
  return null;
}
