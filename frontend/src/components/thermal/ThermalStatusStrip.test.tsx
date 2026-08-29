import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ThermalStatusStrip from './ThermalStatusStrip';

const mocks = vi.hoisted(() => ({ mutateAsync: vi.fn(), reset: vi.fn() }));

vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalStatus: () => ({
    data: {
      mode: 'LwtActive', dhwWriter: 'Legacy', lastTelemetryUtc: new Date().toISOString(),
      overallDataQuality: 'Valid', emhassAvailable: true, planCreatedUtc: new Date().toISOString(),
      planAgeMinutes: 4, currentLwtDeviationC: 0.5, fallbackReason: null,
      nextControlEventUtc: new Date(Date.now() + 900_000).toISOString(), manualOverride: false,
    },
    isError: false,
    isLoading: false,
  }),
  useChangeThermalMode: () => ({
    mutateAsync: mocks.mutateAsync,
    reset: mocks.reset,
    isPending: false,
    isError: false,
    error: null,
  }),
  useThermalReadiness: () => ({ data: { targetMode: 'FullActive', ready: false, checks: [] }, isLoading: false, isError: false }),
}));

describe('ThermalStatusStrip', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.mutateAsync.mockResolvedValue(undefined);
  });

  it('håller rollback synlig och beskriver exakt vad den återställer', async () => {
    const user = userEvent.setup();
    render(<ThermalStatusStrip />);

    await user.click(screen.getByRole('button', { name: 'Rollback' }));
    expect(screen.getByText(/Legacy återtar DHW-skrivrätten/)).toBeInTheDocument();
    expect(screen.getByText(/LWT-avvikelsen nollställs/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Återgå till Legacy' }));
    expect(mocks.mutateAsync).toHaveBeenCalledWith('Legacy');
  });
});
