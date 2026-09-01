import { describe, expect, it } from 'vitest';
import type { ThermalModelVersion, ThermalTelemetrySample } from '../../types/api';
import { modelEvidence, observedCop, parseRecord } from './modelEvidence';

const now = Date.parse('2026-08-31T08:00:00Z');
export const validModel: ThermalModelVersion = {
  id: 1, modelType: '2R2C', isActive: true, createdAtUtc: '2026-08-31T07:00:00Z',
  trainingFromUtc: '2026-08-01T00:00:00Z', trainingToUtc: '2026-08-30T00:00:00Z', parametersJson: '{}', metricsJson: '{}',
  provenance: { verifiable: true, algorithmVersion: 'grey-box-2r2c-v1', selectionVersion: 'thermal-validated-history-v1',
    selectionFromUtc: '2026-07-01T00:00:00Z', selectionToUtc: '2026-08-30T00:00:00Z', observationCount: 2000, trainingSamples: 1600, validationSamples: 400 },
  sourceValidation: { passed: true, status: 'Current', reason: 'Exakt historiskt urval matchar.', checkedAtUtc: new Date(now).toISOString() },
  validation: { passed: true, status: 'Validated', reason: 'Hela prognosfönster klarar kraven.', checkedAtUtc: new Date(now).toISOString(),
    twoHourMaeC: .1, dayMaeC: .2, copMae: null, twoHourValidationWindows: 126, dayValidationWindows: 4 },
};

function sample(minutes = -5): ThermalTelemetrySample {
  return { timestampUtc: new Date(now + minutes * 60_000).toISOString(), heatOutputKw: 4.186, heatPumpPowerKw: 2, cop: 2.093,
    leavingWaterTemperatureC: 35, returnWaterTemperatureC: 30, flowLitresPerMinute: 12, backupHeaterActive: false, defrostActive: false,
    qualityJson: JSON.stringify({ entities: Object.fromEntries(['heat_pump_power', 'leaving_water_temperature', 'return_water_temperature', 'flow', 'backup_heater_active', 'defrost_active']
      .map(role => [role, { Quality: 0, Excluded: false }])) }) } as ThermalTelemetrySample;
}

describe('modelEvidence', () => {
  it('shows explicit full-horizon evidence without equating it to control activation', () => {
    expect(modelEvidence(validModel, now)).toMatchObject({ passed: true, day: .2, dayWindows: 4 });
  });
  it.each(['absent', 'expired', 'future', 'invalid-number', 'missing-windows', 'contradiction'])('rejects %s validation evidence', fault => {
    const model = structuredClone(validModel);
    if (fault === 'absent') delete model.validation;
    if (fault === 'expired') model.validation!.checkedAtUtc = new Date(now - 301_000).toISOString();
    if (fault === 'future') model.validation!.checkedAtUtc = new Date(now + 1).toISOString();
    if (fault === 'invalid-number') model.validation!.dayMaeC = Infinity;
    if (fault === 'missing-windows') model.validation!.dayValidationWindows = 0;
    if (fault === 'contradiction') model.validation!.passed = false;
    expect(modelEvidence(model, now).passed).toBe(false);
  });
  it.each(['absent', 'unknown-algorithm', 'bad-count', 'bad-window'])('rejects %s source provenance even when validation says passed', fault => {
    const model = structuredClone(validModel);
    if (fault === 'absent') delete model.provenance;
    if (fault === 'unknown-algorithm') model.provenance!.algorithmVersion = 'grey-box-2r2c-v2';
    if (fault === 'bad-count') model.provenance!.trainingSamples = 2001;
    if (fault === 'bad-window') model.provenance!.selectionToUtc = '2026-08-01T00:00:00Z';
    expect(modelEvidence(model, now)).toMatchObject({ passed: false, scored: false, sourceVerified: false });
  });
  it.each(['absent', 'changed', 'expired', 'future'])('rejects %s source revalidation even when stored provenance is structurally valid', fault => {
    const model = structuredClone(validModel);
    if (fault === 'absent') delete model.sourceValidation;
    if (fault === 'changed') model.sourceValidation = { ...model.sourceValidation!, passed: false, status: 'Changed', reason: 'Historiken har ändrats.' };
    if (fault === 'expired') model.sourceValidation!.checkedAtUtc = new Date(now - 301_000).toISOString();
    if (fault === 'future') model.sourceValidation!.checkedAtUtc = new Date(now + 1).toISOString();
    expect(modelEvidence(model, now)).toMatchObject({ passed: false, scored: false, sourceVerified: false,
      sourceStatus: fault === 'changed' ? 'changed' : 'unverified' });
  });
  it('does not treat JSON null or arrays as safe parameter records', () => {
    expect(parseRecord('null')).toEqual({});
    expect(parseRecord('[]')).toEqual({});
    expect(parseRecord('broken')).toEqual({});
  });
});

describe('observedCop', () => {
  it('uses summed heat divided by power, not the arithmetic mean of COP', () => {
    const first = sample(-10);
    const second = sample(-5);
    second.heatPumpPowerKw = 1;
    second.cop = 4.186;
    const result = observedCop([first, second], now, true);
    expect(result.count).toBe(2);
    expect(result.value).toBeCloseTo(8.372 / 3);
  });
  it.each(['unverified-sign', 'imported', 'unknown-backup', 'missing-quality', 'future', 'duplicate', 'nonfinite', 'inconsistent'])('rejects %s COP data', fault => {
    const point = sample();
    if (fault === 'imported') point.qualityJson = JSON.stringify({ ...JSON.parse(point.qualityJson), source: 'HomeAssistantHistoryImport' });
    if (fault === 'unknown-backup') point.backupHeaterActive = null;
    if (fault === 'missing-quality') point.qualityJson = '{}';
    if (fault === 'future') point.timestampUtc = new Date(now + 300_000).toISOString();
    if (fault === 'nonfinite') point.cop = NaN;
    if (fault === 'inconsistent') point.cop = 6;
    const result = observedCop(fault === 'duplicate' ? [point, point] : [point], now, fault !== 'unverified-sign');
    expect(result).toEqual({ value: null, count: 0 });
  });
});
