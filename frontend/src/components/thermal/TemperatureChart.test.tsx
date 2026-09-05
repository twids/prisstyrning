import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ThermalTelemetrySample } from '../../types/api';
import TemperatureChart, { temperatureRows } from './TemperatureChart';

vi.mock('@mui/x-charts/LineChart', () => ({ LineChart: () => <div>Diagram</div> }));
describe('temperaturhistorik', () => {
  it('visar tomt läge utan att kräva en optimeringsplan', () => {
    render(<TemperatureChart history={[]} />);
    expect(screen.getByText(/En optimeringsplan behövs inte/)).toBeInTheDocument();
    expect(screen.getByText(/Ingen beräknad LWT-avvikelse/)).toBeInTheDocument();
  });
  it('tar inte med ersatta eller ogiltiga rumsvärden i uppmätt medel', () => {
    const sample = { timestampUtc: '2026-09-05T10:00:00Z', leavingWaterTemperatureC: 31,
      returnWaterTemperatureC: Number.NaN, outsideTemperatureC: null,
      roomTemperaturesJson: '{"valid":21,"fallback":26,"invalid":90}',
      qualityJson: '{"rooms":{"valid":{"Quality":0,"Excluded":false},"fallback":{"Quality":1},"invalid":{"Quality":2}},"heatingDeviationC":0.5}' } as ThermalTelemetrySample;
    expect(temperatureRows([sample])[0]).toMatchObject({ room: 21, lwt: 31, rwt: null, deviation: .5 });
    expect(temperatureRows([{ ...sample, qualityJson: '{}' }])[0].room).toBeNull();
  });
});
