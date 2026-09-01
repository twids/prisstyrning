import { describe, expect, it } from 'vitest';
import { parsePlanInputSnapshot } from './planInputSnapshot';

describe('parsePlanInputSnapshot', () => {
  it('reads explicit actual and estimated planning coverage', () => {
    const result = parsePlanInputSnapshot(JSON.stringify({
      priceForecast: { actualCoverage: .75, actualSteps: 144, estimatedSteps: 48, estimation: 'Föregående dygn.' },
      weatherForecast: { actualCoverage: .5, actualSteps: 96, estimatedSteps: 96, estimation: 'Senaste punkt.' },
      confidenceBasis: 'Modell och indatakvalitet.',
    }));

    expect(result?.priceForecast.estimatedSteps).toBe(48);
    expect(result?.weatherForecast.actualCoverage).toBe(.5);
    expect(result?.confidenceBasis).toBe('Modell och indatakvalitet.');
  });

  it.each([
    '',
    '{}',
    '{bad json',
    JSON.stringify({ priceForecast: .8, weatherForecast: .9 }),
    JSON.stringify({
      priceForecast: { actualCoverage: 2, actualSteps: 1, estimatedSteps: 0, estimation: 'Fel.' },
      weatherForecast: { actualCoverage: 1, actualSteps: 1, estimatedSteps: 0, estimation: 'Ok.' },
      confidenceBasis: 'Fel.',
    }),
  ])('fails closed for malformed or legacy input snapshots', (json) => {
    expect(parsePlanInputSnapshot(json)).toBeNull();
  });
});
