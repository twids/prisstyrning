import { render, screen } from '@testing-library/react';
import { axe } from 'vitest-axe';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ThermalPlan } from '../../types/api';
import ThermalPlanPage from './ThermalPlanPage';

const hooks = vi.hoisted(() => ({ plan: vi.fn(), history: vi.fn() }));
vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalPlan: hooks.plan,
  useThermalHistory: hooks.history,
}));

const now = Date.parse('2026-09-01T08:00:00Z');
const query = (data: unknown) => ({ data, isLoading: false, isError: false, error: null });

function plan(inputSnapshotJson = inputSnapshot()): ThermalPlan {
  return {
    id: 'plan-1',
    userId: 'account-a',
    createdAtUtc: '2026-09-01T07:55:00Z',
    validFromUtc: '2026-09-01T07:45:00Z',
    validUntilUtc: '2026-09-03T07:45:00Z',
    status: 'Valid',
    isShadow: true,
    solverDurationMs: 1250,
    objectiveCost: 12.34,
    confidence: .64,
    summary: 'Husvärmen är optimerad.',
    inputSnapshotJson,
    steps: [{
      id: 1,
      thermalPlanId: 'plan-1',
      startUtc: '2026-09-01T07:45:00Z',
      endUtc: '2026-09-01T08:00:00Z',
      desiredHeatOutputKw: 3,
      desiredLwtDeviationC: 0,
      dhwReserved: false,
      dhwMode: '',
      incrementalCost: .2,
      confidence: .64,
      expectedRoomsJson: '{"representative":21.4}',
      decisionReasonJson: '{"mainReason":"Lågt pris.","price":0.5,"comfortMarginC":0.4,"modelConfidence":0.64,"alternative":null}',
    }],
  };
}

function inputSnapshot() {
  return JSON.stringify({
    priceForecast: { actualCoverage: .75, actualSteps: 144, estimatedSteps: 48, estimation: 'Föregående dygn används.' },
    weatherForecast: { actualCoverage: .5, actualSteps: 96, estimatedSteps: 96, estimation: 'Senaste prognospunkt hålls konstant.' },
    confidenceBasis: 'Modell och verifierad indatatäckning.',
  });
}

describe('ThermalPlanPage', () => {
  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(now);
    hooks.plan.mockReturnValue(query(plan()));
    hooks.history.mockReturnValue(query([]));
  });
  afterEach(() => vi.restoreAllMocks());

  it('explains composite confidence and every estimated 15-minute input step', () => {
    render(<main><ThermalPlanPage /></main>);

    expect(screen.getByText('Planens konfidens')).toBeInTheDocument();
    expect(screen.queryByText('Modellkonfidens')).not.toBeInTheDocument();
    expect(screen.getByText(/Pris 75 % · väder 50 % verifierad täckning/)).toBeInTheDocument();
    expect(screen.getByText(/48 uppskattade prissteg och 96 uppskattade vädersteg à 15 minuter/)).toBeInTheDocument();
    expect(screen.getByText(/Föregående dygn används/)).toBeInTheDocument();
  });

  it('does not invent coverage when an older plan lacks structured evidence', () => {
    hooks.plan.mockReturnValue(query(plan('{}')));
    render(<main><ThermalPlanPage /></main>);

    expect(screen.getByText(/Underlagsdetaljer saknas/)).toBeInTheDocument();
    expect(screen.queryByText(/uppskattade prissteg/)).not.toBeInTheDocument();
  });

  it('keeps the confidence and estimate explanation accessible', async () => {
    const rendered = render(<main><ThermalPlanPage /></main>);
    expect((await axe(rendered.container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([]);
  });
});
