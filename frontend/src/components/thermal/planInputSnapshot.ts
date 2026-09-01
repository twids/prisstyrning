export interface PlanInputCoverage {
  actualCoverage: number;
  actualSteps: number;
  estimatedSteps: number;
  estimation: string;
}

export interface PlanInputSnapshot {
  priceForecast: PlanInputCoverage;
  weatherForecast: PlanInputCoverage;
  confidenceBasis: string;
}

export function parsePlanInputSnapshot(json: string): PlanInputSnapshot | null {
  try {
    const root = JSON.parse(json) as Record<string, unknown>;
    if (!isObject(root)) return null;
    const priceForecast = coverage(root.priceForecast);
    const weatherForecast = coverage(root.weatherForecast);
    const confidenceBasis = typeof root.confidenceBasis === 'string' ? root.confidenceBasis.trim() : '';
    if (!priceForecast || !weatherForecast || !confidenceBasis) return null;
    return { priceForecast, weatherForecast, confidenceBasis };
  } catch {
    return null;
  }
}

function coverage(value: unknown): PlanInputCoverage | null {
  if (!isObject(value)) return null;
  const actualCoverage = value.actualCoverage;
  const actualSteps = value.actualSteps;
  const estimatedSteps = value.estimatedSteps;
  const estimation = typeof value.estimation === 'string' ? value.estimation.trim() : '';
  if (!isFiniteNumber(actualCoverage) || actualCoverage < 0 || actualCoverage > 1 ||
      !isNonNegativeInteger(actualSteps) || !isNonNegativeInteger(estimatedSteps) ||
      actualSteps + estimatedSteps === 0 || !estimation) return null;
  return { actualCoverage, actualSteps, estimatedSteps, estimation };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isNonNegativeInteger(value: unknown): value is number {
  return isFiniteNumber(value) && Number.isInteger(value) && value >= 0;
}
