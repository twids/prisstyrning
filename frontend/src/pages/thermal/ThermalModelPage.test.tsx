import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ThermalModelVersion } from '../../types/api';
import ThermalModelPage from './ThermalModelPage';

const hooks = vi.hoisted(() => ({ models: vi.fn(), history: vi.fn(), config: vi.fn() }));
vi.mock('../../hooks/thermal/useThermal', () => ({ useThermalModels: hooks.models, useThermalHistory: hooks.history, useThermalConfig: hooks.config }));
const now = Date.parse('2026-08-31T08:00:00Z');
const refresh = vi.fn().mockResolvedValue({});
function model(): ThermalModelVersion {
  return { id: 4, modelType: '2R2C', isActive: true, createdAtUtc: '2026-08-31T07:00:00Z', trainingFromUtc: '2026-08-01T00:00:00Z', trainingToUtc: '2026-08-30T00:00:00Z',
    parametersJson: '{"roomAdjustments":null}', metricsJson: '{}', validation: { passed: true, status: 'Validated', reason: 'Hela tvåtimmars- och dygnsfönster klarar kraven.', checkedAtUtc: new Date(now).toISOString(),
      twoHourMaeC: .1, dayMaeC: .2, copMae: null, twoHourValidationWindows: 126, dayValidationWindows: 4 },
    sourceValidation: { passed: true, status: 'Current', reason: 'Exakt historiskt urval matchar.', checkedAtUtc: new Date(now).toISOString() },
    provenance: { verifiable: true, algorithmVersion: 'grey-box-2r2c-v1', selectionVersion: 'thermal-validated-history-v1', selectionFromUtc: '2026-07-01T00:00:00Z',
      buildRevision: '0123456789abcdef0123456789abcdef01234567',
      selectionToUtc: '2026-08-30T00:00:00Z', observationCount: 2000, trainingSamples: 1600, validationSamples: 400 } };
}
const query = (data: unknown) => ({ data, isLoading: false, isError: false, isFetching: false, refetch: refresh });
function view() { return render(<main><ThermalModelPage /></main>); }

describe('ThermalModelPage', () => {
  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(now);
    refresh.mockClear();
    hooks.models.mockReturnValue(query([model()]));
    hooks.history.mockReturnValue(query([]));
    hooks.config.mockReturnValue(query({ site: { heatPumpPowerSignVerified: true } }));
  });
  afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers(); });

  it('separates validated metrics, model data period and control approval in Swedish', () => {
    view();
    expect(screen.getByText('Husmodell: validerad')).toBeInTheDocument();
    expect(screen.getByText('0,10 °C')).toBeInTheDocument();
    expect(screen.getByText('0,20 °C')).toBeInTheDocument();
    expect(screen.getByText(/4 hela 24-timmarsfönster/)).toBeInTheDocument();
    expect(screen.getByText(/inte antal verifierade uppvärmningsdygn/)).toBeInTheDocument();
    expect(screen.getByText(/En validerad modell är inte ett godkännande av aktiv styrning/)).toBeInTheDocument();
    expect(screen.getByText('Validerad · aktivmarkering')).toBeInTheDocument();
    expect(screen.getByText(/Träningsunderlag: omverifierat mot 2[  ]000 valda mätpunkter/)).toBeInTheDocument();
    expect(screen.getByText(/Omverifierat källurval · 2[  ]000 mätpunkter/)).toBeInTheDocument();
  });

  it('fails closed and explains how to repair a model without source provenance', () => {
    const version = model();
    delete version.provenance;
    hooks.models.mockReturnValue(query([version]));
    view();

    expect(screen.getByText('Husmodell: ej verifierad')).toBeInTheDocument();
    expect(screen.getByText(/Träningsunderlag: saknar verifierbart källbevis/)).toBeInTheDocument();
    expect(screen.getByText(/Källbevis saknas · modellen måste tränas om/)).toBeInTheDocument();
    expect(screen.queryByText('Husmodell: validerad')).not.toBeInTheDocument();
  });

  it('distinguishes changed historical source from a model that never had source evidence', () => {
    const version = model();
    version.sourceValidation = { passed: false, status: 'Changed', reason: 'Historiska mätningar har ändrats. Träna en ny version.', checkedAtUtc: new Date(now).toISOString() };
    version.validation = { ...version.validation!, passed: false, status: 'SourceChanged', reason: version.sourceValidation.reason };
    hooks.models.mockReturnValue(query([version]));
    view();

    expect(screen.getByText('Husmodell: ej verifierad')).toBeInTheDocument();
    expect(screen.getByText(/historik eller inställningar har ändrats sedan träningen/)).toBeInTheDocument();
    expect(screen.getByText(/Källunderlaget har ändrats · träna om modellen/)).toBeInTheDocument();
    expect(screen.queryByText(/Källbevis saknas/)).not.toBeInTheDocument();
  });

  it('distinguishes a changed build revision and shows only a short safe commit id', async () => {
    const version = model();
    version.sourceValidation = { passed: false, status: 'BuildChanged', reason: 'Modellen tränades med en annan kodrevision.', checkedAtUtc: new Date(now).toISOString() };
    version.validation = { ...version.validation!, passed: false, status: 'BuildChanged', reason: version.sourceValidation.reason };
    hooks.models.mockReturnValue(query([version]));
    const rendered = view();

    expect(screen.getByText(/modellen hör till en annan kodrevision/)).toBeInTheDocument();
    expect(screen.getByText(/Kodrevisionen har ändrats · träna om modellen/)).toBeInTheDocument();
    hooks.models.mockReturnValue(query([model()]));
    rendered.rerender(<main><ThermalModelPage /></main>);
    const advanced = screen.getByRole('button', { name: /Avancerat: husmodell och rumskalibrering/ });
    await userEvent.click(advanced);
    expect(screen.getByText('Byggrevision: 0123456789ab')).toBeInTheDocument();
    expect(screen.queryByText('0123456789abcdef0123456789abcdef01234567')).not.toBeInTheDocument();
  });

  it.each(['legacy', 'invalid', 'insufficient', 'expired', 'malformed-number'])('does not present %s active flags as validated', fault => {
    const version = model();
    if (fault === 'legacy') delete version.validation;
    if (fault === 'invalid' || fault === 'insufficient') version.validation = { ...version.validation!, passed: false, status: fault === 'invalid' ? 'Invalid' : 'Insufficient', reason: 'Underlaget behöver tränas om.' };
    if (fault === 'expired') version.validation!.checkedAtUtc = new Date(now - 301_000).toISOString();
    if (fault === 'malformed-number') version.validation!.dayMaeC = Infinity;
    hooks.models.mockReturnValue(query([version]));
    view();

    expect(screen.getByText('Husmodell: ej verifierad')).toBeInTheDocument();
    expect(screen.getByText('Ej verifierad · aktivmarkering')).toBeInTheDocument();
    expect(screen.queryByText('Husmodell: validerad')).not.toBeInTheDocument();
    expect(screen.queryByText('0,20 °C')).not.toBeInTheDocument();
    expect(screen.queryByText(/Infinity|NaN/)).not.toBeInTheDocument();
  });

  it('revokes a cached approval when model fetching fails and offers read-only retry', async () => {
    hooks.models.mockReturnValue({ ...query([model()]), isError: true, error: new Error('private-model-response') });
    view();
    expect(screen.getByText(/Modellunderlaget kunde inte hämtas/)).toBeInTheDocument();
    expect(screen.queryByText('Husmodell: validerad')).not.toBeInTheDocument();
    expect(screen.queryByText('2R2C · version 4')).not.toBeInTheDocument();
    expect(screen.queryByText(/private-model-response/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Hämta underlag igen' }));
    expect(refresh).toHaveBeenCalledTimes(3);
  });

  it('expires an approval even without a new query result', () => {
    vi.useFakeTimers();
    vi.setSystemTime(now);
    view();
    act(() => vi.advanceTimersByTime(330_000));
    expect(screen.getByText('Husmodell: ej verifierad')).toBeInTheDocument();
    expect(screen.queryByText('Husmodell: validerad')).not.toBeInTheDocument();
  });

  it('handles null JSON and malformed dates without crashing or exposing raw data', () => {
    hooks.models.mockReturnValue(query([{ ...model(), createdAtUtc: 'invalid', trainingToUtc: 'invalid', parametersJson: 'null', metricsJson: 'null', validation: undefined }]));
    view();
    expect(screen.getByRole('heading', { name: 'Modell' })).toBeInTheDocument();
    expect(screen.getByText(/Okänd tid/)).toBeInTheDocument();
    expect(screen.queryByText('Husmodell: validerad')).not.toBeInTheDocument();
  });

  it('keeps failed candidates visible without calling them archived or active control', () => {
    const candidate = { ...model(), id: 5, isActive: false, createdAtUtc: '2026-08-31T07:30:00Z', validation: { ...model().validation!, passed: false, status: 'ThresholdExceeded' as const, dayMaeC: .8, reason: 'Dygnsfelet är för stort.' } };
    hooks.models.mockReturnValue(query([model(), candidate]));
    view();
    expect(screen.getByText('2R2C · version 5')).toBeInTheDocument();
    expect(screen.getByText('Dygnsfelet är för stort.')).toBeInTheDocument();
    expect(screen.queryByText('Arkiverad')).not.toBeInTheDocument();
  });

  it('does not infer empty or valid COP history from failed reads', () => {
    hooks.history.mockReturnValue({ ...query([]), isError: true });
    view();
    expect(screen.getByText(/Ingen observerad COP kan verifieras just nu/)).toBeInTheDocument();
  });

  it('has accessible headings, version list, explanations and controls', async () => {
    const rendered = view();
    expect((await axe(rendered.container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([]);
  });
});
