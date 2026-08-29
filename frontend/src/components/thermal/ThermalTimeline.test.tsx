import { render, screen } from '@testing-library/react';
import { axe } from 'vitest-axe';
import { describe, expect, it } from 'vitest';
import type { ThermalPlan, ThermalTelemetrySample } from '../../types/api';
import ThermalTimeline from './ThermalTimeline';

const start = new Date('2026-10-25T00:00:00Z');
const steps = Array.from({ length: 8 }, (_, index) => ({
  id: index + 1,
  thermalPlanId: 'plan-1',
  startUtc: new Date(start.getTime() + index * 900_000).toISOString(),
  endUtc: new Date(start.getTime() + (index + 1) * 900_000).toISOString(),
  desiredHeatOutputKw: 3,
  desiredLwtDeviationC: index % 2 ? 0.5 : 0,
  dhwReserved: index === 3,
  dhwMode: index === 3 ? 'Eco' : '',
  incrementalCost: 0.4,
  confidence: 0.85,
  expectedRoomsJson: JSON.stringify({ representative: 21.2 }),
  decisionReasonJson: JSON.stringify({ mainReason: 'Komfort inom bandet.', price: 0.8, comfortMarginC: 0.4, modelConfidence: 0.85, alternative: null }),
}));

const plan: ThermalPlan = {
  id: 'plan-1', userId: 'default', createdAtUtc: start.toISOString(), validFromUtc: start.toISOString(),
  validUntilUtc: new Date(start.getTime() + 8 * 900_000).toISOString(), status: 'Valid', isShadow: true,
  solverDurationMs: 1200, objectiveCost: 14.2, confidence: 0.85, summary: 'Shadowplan', inputSnapshotJson: '{}', steps,
};

const history: ThermalTelemetrySample[] = [{
  id: 1, userId: 'default', timestampUtc: start.toISOString(), outsideTemperatureC: 3,
  outsideTemperatureForecastJson: '[]', windSpeedMps: null, solarIrradianceWm2: null,
  leavingWaterTemperatureC: 34, returnWaterTemperatureC: 30, flowLitresPerMinute: 12,
  brineInC: 2, brineOutC: 0, tankTemperatureC: 48, heatPumpPowerKw: 1.2,
  propertyPowerKw: 2, spotPriceSekPerKwh: 0.72, heatOutputKw: 4, cop: 3.3, dhwActive: false, defrostActive: false,
  backupHeaterActive: false, roomTemperaturesJson: '{"sensor.room":21.1}', qualityJson: '{}',
}];

describe('ThermalTimeline', () => {
  it('skiljer faktiskt, prognos, shadow och DHW även utan färg', () => {
    render(<ThermalTimeline plan={plan} history={history} />);

    expect(screen.getByText('Faktiskt – heldragen')).toBeInTheDocument();
    expect(screen.getByText('Prognos – streckad')).toBeInTheDocument();
    expect(screen.getByText('Shadow – prickad markör')).toBeInTheDocument();
    expect(screen.getByText('DHW – skrafferat fält')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Zoom- och panorerbar värmeplan' })).toHaveAttribute('tabindex', '0');
    expect(screen.getByText(/92, 96 och 100 kvartperioder/)).toBeInTheDocument();
  });

  it('har inga automatiskt identifierade tillgänglighetsfel', async () => {
    const { container } = render(<ThermalTimeline plan={plan} history={history} />);
    const result = await axe(container, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });
});
