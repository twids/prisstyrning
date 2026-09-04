import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DataQuality, ThermalStatus } from '../../types/api';
import ThermalStatusStrip from './ThermalStatusStrip';

const mocks = vi.hoisted(() => ({ mutateAsync: vi.fn(), reset: vi.fn(), status: vi.fn() }));
const now = Date.parse('2026-08-31T02:00:00Z');
const data: ThermalStatus = {
  mode: 'LwtActive', dhwWriter: 'Legacy', lastTelemetryUtc: new Date(now - 60_000).toISOString(),
  overallDataQuality: 'Valid', emhassAvailable: true, planCreatedUtc: new Date(now - 240_000).toISOString(),
  planAgeMinutes: 4, currentLwtDeviationC: 0.5, fallbackReason: null,
  nextControlEventUtc: new Date(now + 900_000).toISOString(), manualOverride: false,
};

vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalStatus: mocks.status,
  useChangeThermalMode: () => ({
    mutateAsync: mocks.mutateAsync,
    reset: mocks.reset,
    isPending: false,
    isError: false,
    error: null,
  }),
  useThermalReadiness: () => ({ data: { targetMode: 'FullActive', ready: false, checks: [] }, isLoading: false, isError: false }),
}));

function renderStatus() {
  return render(<MemoryRouter><ThermalStatusStrip /></MemoryRouter>);
}

describe('ThermalStatusStrip', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.mutateAsync.mockResolvedValue(undefined);
    mocks.status.mockReturnValue({ data, isError: false, isLoading: false });
    vi.spyOn(Date, 'now').mockReturnValue(now);
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('håller rollback synlig och beskriver exakt vad den återställer', async () => {
    const user = userEvent.setup();
    renderStatus();

    await user.click(screen.getByRole('button', { name: 'Rollback' }));
    expect(screen.getByText(/Legacy återtar DHW-skrivrätten/)).toBeInTheDocument();
    expect(screen.getByText(/LWT-avvikelsen nollställs/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Återgå till Legacy' }));
    expect(mocks.mutateAsync).toHaveBeenCalledWith('Legacy');
  });

  it.each([
    ['Invalid', 'Ogiltig'], ['Unavailable', 'Saknas'], ['Stale', 'Gammal'],
  ])('visar serverns %s med förklaring trots färsk tidsstämpel', (quality, label) => {
    mocks.status.mockReturnValue({ data: { ...data, overallDataQuality: quality, dataQualityReason: '1/2 datakällor är giltiga; en givare är exkluderad.' }, isError: false });
    renderStatus();
    expect(screen.getByText(label, { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('Giltig', { exact: true })).not.toBeInTheDocument();
    expect(screen.getByText(/en givare är exkluderad/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'rum' })).toHaveAttribute('href', '/rooms');
    expect(screen.getByRole('link', { name: 'givarmappning' })).toHaveAttribute('href', '/settings');
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('visar återhämtad kvalitet utan att kalla det godkänd komfort eller aktivering', () => {
    mocks.status.mockReturnValue({ data: { ...data, overallDataQuality: 'Invalid', dataQualityReason: 'Givare exkluderad.' }, isError: false });
    const view = renderStatus();
    expect(screen.getByText('Ogiltig', { exact: true })).toBeInTheDocument();
    mocks.status.mockReturnValue({ data: { ...data, dataQualityReason: 'Alla 2 aktiverade datakällor är giltiga i senaste insamlingen.' }, isError: false });
    view.rerender(<MemoryRouter><ThermalStatusStrip /></MemoryRouter>);
    expect(screen.getByText('Giltig', { exact: true })).toBeInTheDocument();
    expect(screen.queryByText(/Givare exkluderad/)).not.toBeInTheDocument();
    expect(screen.getByText(/Komfort och tillåtelse till aktiv styrning bedöms separat/)).toBeInTheDocument();
    expect(screen.getByText('LWT 0,5 °C')).toBeInTheDocument();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('döljer cachelagrad giltig status vid hämtningsfel men behåller rollbackvägen', async () => {
    mocks.status.mockReturnValue({ data, isError: true });
    const user = userEvent.setup();
    renderStatus();
    expect(screen.getByRole('alert')).toHaveTextContent('Aktuellt driftläge och datakvalitet kan inte bekräftas.');
    expect(screen.queryByText('Giltig', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText('LWT 0,5 °C')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Byt läge' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Rollback' }));
    expect(screen.getByText(/Legacy återtar DHW-skrivrätten/)).toBeInTheDocument();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('hämtar status utan att först påstå Legacy, giltig data eller att EMHASS är nere', () => {
    mocks.status.mockReturnValue({ data: undefined, isLoading: true, isError: false });
    renderStatus();
    expect(screen.getByRole('status')).toHaveTextContent('Hämtar systemstatus');
    expect(screen.queryByText('Giltig', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText(/EMHASS/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Byt läge' })).not.toBeInTheDocument();
  });

  it.each([
    [600, 'Giltig'], [601, 'Gammal'], [-1, 'Ogiltig'],
  ])('värderar sparad giltig status vid åldern %s sekunder', (seconds, label) => {
    mocks.status.mockReturnValue({ data: { ...data, lastTelemetryUtc: new Date(now - seconds * 1000).toISOString() }, isError: false });
    renderStatus();
    expect(screen.getByText(label, { exact: true })).toBeInTheDocument();
  });

  it.each([
    [null, 'Saknas'], ['not-a-date', 'Ogiltig'],
  ])('behandlar saknad eller ogiltig insamlingstid som osäker: %s', (timestamp, label) => {
    mocks.status.mockReturnValue({ data: { ...data, lastTelemetryUtc: timestamp }, isError: false });
    renderStatus();
    expect(screen.getByText(label!, { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('Giltig', { exact: true })).not.toBeInTheDocument();
  });

  it('låter inte Giltig leva vidare i en cache när nätpollning har stannat', () => {
    vi.useFakeTimers();
    renderStatus();
    expect(screen.getByText('Giltig', { exact: true })).toBeInTheDocument();
    vi.spyOn(Date, 'now').mockReturnValue(now + 11 * 60_000);
    act(() => { vi.advanceTimersByTime(30_000); });
    expect(screen.getByText('Gammal', { exact: true })).toBeInTheDocument();
    expect(screen.queryByText('Giltig', { exact: true })).not.toBeInTheDocument();
  });

  it.each<DataQuality>(['Valid', 'Invalid'])('har automatiskt kontrollerad tillgänglighet med kvalitet %s', async overallDataQuality => {
    mocks.status.mockReturnValue({ data: { ...data, overallDataQuality }, isError: false });
    renderStatus();
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });
});
