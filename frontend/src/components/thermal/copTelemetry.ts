import type { ThermalConfig, ThermalTelemetrySample } from '../../types/api';
import { finite, parseRecord, record } from './modelEvidence';

export const copPeriods: Record<string, string> = { Unknown: 'Okänd medelperiod', Day: 'Dygn', Week: 'Vecka', Month: 'Månad', Year: 'År', Lifetime: 'Livstid', SinceReset: 'Sedan nollställning' };

export function copTelemetry(history: ThermalTelemetrySample[] | undefined, config: ThermalConfig | undefined, now: number) {
  const mappings = config?.entities?.filter(entity => entity.enabled) ?? [];
  const external = mappings.some(entity => entity.role === 'cop_realtime');
  const average = mappings.find(entity => entity.role === 'cop_average');
  const latest = [...history ?? []].filter(sample => Date.parse(sample.timestampUtc) <= now)
    .sort((a, b) => Date.parse(b.timestampUtc) - Date.parse(a.timestampUtc))[0];
  const metadata = parseRecord(latest?.qualityJson);
  const current = latest && now - Date.parse(latest.timestampUtc) <= 600_000 &&
    Date.parse(config?.site.updatedAtUtc ?? '') <= Date.parse(latest.timestampUtc) && !metadata.source;
  function read(role: string) {
    const value = record(record(metadata.entities)[role]);
    const quality = value.Quality ?? value.quality;
    const usage = value.Usage ?? value.usage;
    const updated = value.ValueUpdatedUtc ?? value.valueUpdatedUtc;
    const excluded = value.Excluded ?? value.excluded;
    const sourceId = role === 'cop_realtime' ? metadata.copEntityId : metadata.copAverageEntityId;
    const mapping = mappings.find(entity => entity.role === role);
    const updatedAt = typeof updated === 'string' ? Date.parse(updated) : NaN;
    const valid = current && mapping && mapping.entityId === sourceId && excluded === false &&
      Number.isFinite(updatedAt) && updatedAt <= now && updatedAt <= Date.parse(latest.timestampUtc) + 300_000 &&
      [0, 1, 'Valid', 'Stale'].includes(quality as string | number);
    return { value: valid ? finite(value.Value ?? value.value) : null,
      updated: valid && typeof updated === 'string' ? updated : null,
      historical: usage !== 'Current' || ![0, 'Valid'].includes(quality as string | number) };
  }
  return { external, hasAverage: Boolean(average), realtime: read('cop_realtime'), average: read('cop_average'),
    period: copPeriods[average?.averagingPeriod ?? 'Unknown'] ?? copPeriods.Unknown };
}
