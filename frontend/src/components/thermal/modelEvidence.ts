import type { ThermalModelVersion, ThermalTelemetrySample } from '../../types/api';

export function record(value: unknown): Record<string, unknown> {
  return value != null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

export function parseRecord(json: string | undefined): Record<string, unknown> {
  try { return record(JSON.parse(json ?? 'null')); } catch { return {}; }
}

export function finite(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

export function modelEvidence(model: ThermalModelVersion | undefined, now: number) {
  const value = model?.validation;
  const checked = Date.parse(value?.checkedAtUtc ?? '');
  const current = Number.isFinite(checked) && checked <= now && now - checked <= 5 * 60_000;
  const count = (input: unknown) => typeof input === 'number' && Number.isSafeInteger(input) && input > 0;
  const twoHour = finite(value?.twoHourMaeC);
  const day = finite(value?.dayMaeC);
  const cop = finite(value?.copMae);
  const metricsValid = model?.modelType === '2R2C'
    ? twoHour != null && twoHour >= 0 && day != null && day >= 0 && count(value?.twoHourValidationWindows) && count(value?.dayValidationWindows)
    : model?.modelType === 'COP' && cop != null && cop >= 0;
  const scored = current && metricsValid && (value?.status === 'Validated' || value?.status === 'ThresholdExceeded');
  const passed = scored && value?.passed === true && value.status === 'Validated' &&
    (model?.modelType === '2R2C' ? twoHour! <= .3 && day! <= .6 : cop! <= .5);
  const knownBlocked = current && ['Missing', 'Invalid', 'Unproven', 'Insufficient', 'ThresholdExceeded'].includes(value?.status ?? '');
  const reason = (passed || knownBlocked) && typeof value?.reason === 'string' && value.reason
    ? value.reason : 'Valideringsunderlaget saknas, är för gammalt eller kan inte verifieras. Hämta det igen; äldre modeller kan behöva tränas om.';
  return { passed, scored, reason, twoHour: scored ? twoHour : null, day: scored ? day : null, cop: scored ? cop : null,
    twoHourWindows: scored ? value?.twoHourValidationWindows : null, dayWindows: scored ? value?.dayValidationWindows : null };
}

function field(value: unknown, name: string): unknown {
  const entries = Object.entries(record(value)).filter(([key]) => key.toLowerCase() === name.toLowerCase());
  return entries.length === 1 ? entries[0][1] : undefined;
}

function validAssessment(value: unknown) {
  const quality = field(value, 'quality');
  return field(value, 'excluded') === false && (quality === 0 || typeof quality === 'string' && quality.toLowerCase() === 'valid');
}

export function observedCop(samples: ThermalTelemetrySample[] | undefined, now: number, signVerified: boolean) {
  if (!signVerified) return { value: null, count: 0 };
  const timestamps = new Map<number, number>();
  for (const sample of samples ?? []) {
    const time = Date.parse(sample.timestampUtc);
    timestamps.set(time, (timestamps.get(time) ?? 0) + 1);
  }
  const valid = (samples ?? []).filter(sample => {
    const time = Date.parse(sample.timestampUtc);
    if (!Number.isFinite(time) || time > now || now - time > 24 * 60 * 60_000 || time % 300_000 !== 0 || timestamps.get(time) !== 1) return false;
    const quality = parseRecord(sample.qualityJson);
    if (Object.keys(quality).some(key => key.toLowerCase() === 'source')) return false;
    const entities = field(quality, 'entities');
    if (!['heat_pump_power', 'leaving_water_temperature', 'return_water_temperature', 'flow', 'backup_heater_active', 'defrost_active']
      .every(role => validAssessment(field(entities, role)))) return false;
    const cop = finite(sample.cop);
    const heat = finite(sample.heatOutputKw);
    const power = finite(sample.heatPumpPowerKw);
    const flow = finite(sample.flowLitresPerMinute);
    const lwt = finite(sample.leavingWaterTemperatureC);
    const rwt = finite(sample.returnWaterTemperatureC);
    if (sample.backupHeaterActive !== false || sample.defrostActive !== false || cop == null || cop < 1.2 || cop > 8 ||
      heat == null || heat <= .5 || power == null || power <= .1 || flow == null || flow <= 0 || lwt == null || rwt == null) return false;
    const derived = flow / 60 * 4.186 * (lwt - rwt);
    return Number.isFinite(derived) && derived > .5 && Math.abs(derived - heat) <= Math.max(.05, derived * .01) && Math.abs(heat / power - cop) <= .01;
  });
  const heat = valid.reduce((sum, sample) => sum + sample.heatOutputKw!, 0);
  const power = valid.reduce((sum, sample) => sum + sample.heatPumpPowerKw!, 0);
  const ratio = heat / power;
  return { value: valid.length && Number.isFinite(ratio) ? ratio : null, count: valid.length };
}
