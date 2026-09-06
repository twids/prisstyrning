import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { axe } from 'vitest-axe';
import type { ThermalConfig, ThermalEntityConfig } from '../../types/api';
import { SensorRequirement, SensorRequirementsSummary, sensorRequirements } from './SensorRequirement';

const labels = Object.keys(sensorRequirements).map(role => [role, role, ''] as const);
const entity = (role: string, enabled = true, entityId = `sensor.${role}`): ThermalEntityConfig => ({
  id: 1, userId: 'test', role, entityId, enabled, expectedUnit: '', minimumValid: null, maximumValid: null, maximumRatePerHour: null,
});
const config = (entities: ThermalEntityConfig[] = []): ThermalConfig => ({
  site: { tariffEnabled: false } as ThermalConfig['site'], entities, rooms: [],
});

describe('sensorernas krav och nytta', () => {
  it('skiljer dagens tolv planeringskrav från senare styrning och valfria källor', () => {
    expect(Object.entries(sensorRequirements).filter(([, value]) => value.required).map(([role]) => role).sort()).toEqual([
      'outside_temperature', 'leaving_water_temperature', 'return_water_temperature', 'flow', 'dhw_active',
      'defrost_active', 'brine_in', 'tank_temperature', 'backup_heater_active', 'heat_pump_power', 'property_power', 'weather_forecast',
    ].sort());
    expect(sensorRequirements.heating_deviation.label).toBe('Inför aktiv LWT-styrning');
    expect(sensorRequirements.cop_realtime.label).toBe('Alternativ COP-källa');
    expect(sensorRequirements.cop_average.label).toBe('Valfri uppföljning');
    expect(sensorRequirements.wind_speed.label).toBe('Valfri förbättring');
  });

  it('räknar bara aktiverade och ifyllda obligatoriska källor i utkastet', () => {
    render(<SensorRequirementsSummary labels={labels} draft={config([
      entity('outside_temperature'), entity('flow', false), entity('dhw_active', true, '   '), entity('cop_average'),
    ])} />);
    expect(screen.getByRole('status')).toHaveTextContent('1 av 12 nödvändiga datakällor');
    expect(screen.getByText(/Saknas eller avstängda:/)).toHaveTextContent('flow');
    expect(screen.getByText(/Saknas eller avstängda:/)).not.toHaveTextContent('outside_temperature');
    expect(screen.getByText(/Under Rum behövs också/)).toBeInTheDocument();
    expect(screen.getByText(/Du kan spara stegvis/)).toBeInTheDocument();
  });

  it('gör inte alla valda givare till ett godkännande av aktiv styrning', () => {
    const draft = config(labels.map(([role]) => entity(role)));
    draft.rooms = [{ enabled: true, isCritical: true, entityId: 'sensor.room' } as ThermalConfig['rooms'][number]];
    render(<SensorRequirementsSummary labels={labels} draft={draft} />);
    expect(screen.getByRole('status')).toHaveTextContent('12 av 12');
    expect(screen.getByRole('status')).toHaveTextContent('inte ett godkännande av mätdata, modell eller aktiv styrning');
    expect(screen.getByText('Minst ett kritiskt rum har en vald givare.')).toBeInTheDocument();
    expect(screen.queryByText(/Saknas eller avstängda:/)).not.toBeInTheDocument();
  });

  it('visar kvarvarande effektkrav även med extern COP och tariff av', () => {
    render(<SensorRequirementsSummary labels={labels} draft={config([entity('cop_realtime')])} />);
    expect(screen.getByText(/Saknas eller avstängda:/)).toHaveTextContent('heat_pump_power');
    expect(screen.getByText(/Saknas eller avstängda:/)).toHaveTextContent('property_power');
  });

  it('anger saknat respektive valt utan att lova giltig data', () => {
    const view = render(<SensorRequirement role="flow" selected={false} />);
    expect(screen.getByText(/Saknas eller är avstängd/)).toBeInTheDocument();
    view.rerender(<SensorRequirement role="flow" selected />);
    expect(screen.getByText('Datakälla vald – kvalitet kontrolleras separat.')).toBeInTheDocument();
    expect(screen.queryByText(/Saknas eller är avstängd/)).not.toBeInTheDocument();
  });

  it('förklarar bergvärmebegränsningen i stället för att föreslå påhittade mätvärden', () => {
    render(<SensorRequirement role="defrost_active" selected={false} />);
    expect(screen.getByText(/För bergvärme utan avfrostning/)).toHaveTextContent('Ingen HA-hjälpsensor behövs');
  });

  it('har läsbara krav utan att förlita sig på enbart färg', async () => {
    const { container } = render(<SensorRequirementsSummary labels={labels} draft={config()} />);
    expect(screen.getByRole('region', { name: 'Vad behöver fyllas i?' })).toBeInTheDocument();
    expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([]);
  });
});
