import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { HomeAssistantEntity } from '../../types/api';
import WeatherSourcePicker from './WeatherSourcePicker';

const result = vi.hoisted(() => ({ data: undefined as unknown, isError: false, isPending: false, mutate: vi.fn() }));
vi.mock('@tanstack/react-query', () => ({ useMutation: () => result }));
describe('väderval', () => {
  it('bevarar sparad källa när sensorlistan saknas', () => {
    result.data = undefined;
    render(<WeatherSourcePicker catalog={{ entities: [], nowUtc: Date.now() }} entityId="weather.home" onChange={vi.fn()} />);
    expect(screen.getByText(/Sparad källa: weather.home/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Testa väderprognos' })).toBeEnabled();
  });
  it('visar verifierad täckning och saknad sol utan att lova 48 timmar', () => {
    result.data = { quality: 'Valid', points: [0, 1].map((hour) => ({ timestampUtc: `2026-09-05T1${hour}:00:00Z`, temperatureC: 15, windSpeedMps: 5, solarIrradianceWm2: null })) };
    render(<WeatherSourcePicker catalog={{ entities: [{ entityId: 'weather.home', friendlyName: 'Väder' } as HomeAssistantEntity], nowUtc: Date.now() }} entityId="weather.home" onChange={vi.fn()} />);
    expect(screen.getByText(/2 giltiga prognospunkter/)).toHaveTextContent('Solinstrålning finns i 0 punkter');
    expect(screen.getByText(/garanterar inte 48 timmars täckning/)).toBeInTheDocument();
  });
});
