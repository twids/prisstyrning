import { describe, expect, it } from 'vitest';
import type { ThermalConfig, ThermalTelemetrySample } from '../../types/api';
import { copTelemetry } from './copTelemetry';

const now = Date.parse('2026-09-06T12:00:00Z');
const config = { site: { updatedAtUtc: '2026-09-05T12:00:00Z' }, entities: [
  { enabled: true, role: 'cop_realtime', entityId: 'sensor.cop' },
  { enabled: true, role: 'cop_average', entityId: 'sensor.average', averagingPeriod: 'Lifetime' },
] } as ThermalConfig;
const metadata = { copSource: 'HomeAssistantRealtime', copEntityId: 'sensor.cop', copAverageEntityId: 'sensor.average',
  entities: { cop_realtime: { Quality: 1, Excluded: false, Value: 4.2, Usage: 'HeldWhileIdle', ValueUpdatedUtc: '2026-09-06T05:00:00Z' },
    cop_average: { Quality: 1, Excluded: false, Value: 3.8, Usage: 'HistoricalAverage', ValueUpdatedUtc: '2026-09-06T05:00:00Z' } } };
const sample = { timestampUtc: '2026-09-06T12:00:00Z', qualityJson: JSON.stringify(metadata), cop: null } as ThermalTelemetrySample;

describe('COP från separata HA-entities', () => {
  it('visar realtidsvärdet som historiskt vid vila och medelperioden separat', () => {
    const result = copTelemetry([sample], config, now);
    expect(result.realtime).toMatchObject({ value: 4.2, historical: true, updated: '2026-09-06T05:00:00Z' });
    expect(result.average.value).toBe(3.8);
    expect(result.period).toBe('Livstid');
  });
  it('gör ingen egen COP-reservberäkning när valt realtidsvärde saknas', () => {
    const result = copTelemetry([{ ...sample, qualityJson: '{}' }], config, now);
    expect(result.external).toBe(true);
    expect(result.realtime.value).toBeNull();
  });
  it('utgången insamling, import eller nytt kontourval visas inte som aktuell livekälla', () => {
    expect(copTelemetry([sample], config, now + 660_000).realtime.value).toBeNull();
    expect(copTelemetry([{ ...sample, qualityJson: JSON.stringify({ ...metadata, source: 'HomeAssistantHistoryImport' }) }], config, now).realtime.value).toBeNull();
    expect(copTelemetry([sample], { ...config, entities: [{ ...config.entities[0], entityId: 'sensor.other' }] }, now).realtime.value).toBeNull();
  });
  it('hanterar ofullständiga äldre svar och okänd medelperiod', () => {
    expect(copTelemetry([], {} as ThermalConfig, now).realtime.value).toBeNull();
    expect(copTelemetry([], { ...config, entities: [{ ...config.entities[1], averagingPeriod: null }] }, now).period).toBe('Okänd medelperiod');
  });
  it('visar inte ogiltiga, exkluderade eller framtida värden', () => {
    for (const changes of [{ Quality: 2 }, { Excluded: true }, { ValueUpdatedUtc: '2099-01-01T00:00:00Z' }]) {
      const json = { ...metadata, entities: { ...metadata.entities, cop_realtime: { ...metadata.entities.cop_realtime, ...changes } } };
      expect(copTelemetry([{ ...sample, qualityJson: JSON.stringify(json) }], config, now).realtime.value).toBeNull();
    }
  });
});
