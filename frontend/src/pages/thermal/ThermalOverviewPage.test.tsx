import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import ThermalOverviewPage from './ThermalOverviewPage';

const hooks = vi.hoisted(() => ({ config: vi.fn() }));
vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalConfig: hooks.config,
  useThermalStatus: () => ({ data: { mode: 'Shadow', dhwWriter: 'Legacy', currentLwtDeviationC: 0 } }),
  useThermalHistory: () => ({ data: [] }), useThermalEvents: () => ({ data: [] }),
  useThermalReadiness: () => ({ data: { checks: [], ready: false } }),
}));

describe('ThermalOverviewPage COP', () => {
  it.each([true, false])('explains the selected COP source without changing controls (external=%s)', external => {
    hooks.config.mockReturnValue({ data: { site: { heatPumpPowerSignVerified: false }, entities: external
      ? [{ role: 'cop_realtime', entityId: 'sensor.cop', enabled: true }] : [] } });
    render(<MemoryRouter><ThermalOverviewPage /></MemoryRouter>);
    expect(screen.getByText(external ? 'Realtids-COP från Home Assistant' : 'Beräknad COP')).toBeInTheDocument();
    if (external) {
      expect(screen.queryByText(/kräver verifierad effektmätning/)).not.toBeInTheDocument();
      expect(screen.getByText(/ingen dold reservberäkning/)).toBeInTheDocument();
    } else expect(screen.getByText(/Egen COP-beräkning kräver verifierad effektmätning/)).toBeInTheDocument();
  });
});
