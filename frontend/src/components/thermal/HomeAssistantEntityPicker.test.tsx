import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { describe, expect, it, vi } from 'vitest';
import type { HomeAssistantEntity } from '../../types/api';
import HomeAssistantEntityPicker, { type EntityCatalogView } from './HomeAssistantEntityPicker';

const now = Date.now();
const iso = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
const temperature: HomeAssistantEntity = {
  entityId: 'sensor.room_temperature', friendlyName: 'Vardagsrum', state: '68', unit: '°F',
  lastUpdatedUtc: iso(-1), receivedAtUtc: iso(0), quality: 'Valid', qualityReason: null,
  compatibleUnits: ['°C'], checkedAtUtc: iso(0), validUntilUtc: iso(9),
};
const power: HomeAssistantEntity = {
  ...temperature, entityId: 'sensor.heat_pump', friendlyName: 'Värmepump', state: '1500', unit: 'W', compatibleUnits: ['kW'],
};
const catalog: EntityCatalogView = { entities: [temperature, power], nowUtc: now };

function picker(view = catalog, entityId = temperature.entityId, onChange = vi.fn(), expectedUnit = '°C') {
  return <HomeAssistantEntityPicker catalog={view} entityId={entityId} expectedUnit={expectedUnit} label="Temperaturentity" onChange={onChange} />;
}

describe('Home Assistant-väljare', () => {
  it('skiljer gammal ändring från färsk rapportering i den tillgängliga statusen', () => {
    render(picker({ ...catalog, entities: [{ ...temperature, lastUpdatedUtc: iso(-240), lastReportedUtc: iso(-1),
      qualityReason: 'Oförändrat värde med aktuell rapportering från HA-integrationen.' }] }));
    expect(screen.getByRole('status')).toHaveTextContent('HA uppdaterad');
    expect(screen.getByRole('status')).toHaveTextContent('Senast rapporterat av HA-integrationen');
    expect(screen.getByRole('status')).toHaveTextContent('Oförändrat värde');
    expect(screen.getByText('Värde/enhet OK')).toBeInTheDocument();
  });

  it('visar namn, ID, råvärde, enhet, båda tiderna och preliminär kontroll efter valet', () => {
    render(picker());
    expect(screen.getByRole('combobox', { name: 'Temperaturentity' })).toHaveValue('Vardagsrum · sensor.room_temperature');
    expect(screen.getByText('sensor.room_temperature', { exact: true })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('68 °F');
    expect(screen.getByRole('status')).toHaveTextContent('HA uppdaterad');
    expect(screen.getByRole('status')).toHaveTextContent('mottaget');
    expect(screen.getByText('Värde/enhet OK')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Historik och rimlighet bedöms separat');
  });

  it('visar ett begripligt enhetsfel direkt för en sparad mappning', () => {
    render(picker(catalog, power.entityId));
    expect(screen.getByText('Ogiltig')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Värdet kan inte läsas som °C');
    expect(screen.getByRole('combobox')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.queryByText('Värde/enhet OK')).not.toBeInTheDocument();
  });

  it.each(['unknown', 'unavailable'])('visar aldrig färskt %s som godkänt', (state) => {
    render(picker({ ...catalog, entities: [{ ...temperature, state }] }));
    expect(screen.getByRole('status')).toHaveTextContent('Saknas');
    expect(screen.getByRole('status')).toHaveTextContent('saknar ett tillgängligt värde');
    expect(screen.queryByText('Värde/enhet OK')).not.toBeInTheDocument();
  });

  it('bevarar en sparad entity som saknas i listan utan att ändra konfigurationen', () => {
    const change = vi.fn();
    render(picker({ ...catalog, entities: [] }, 'sensor.preserved_mapping', change));
    expect(screen.getByRole('combobox')).toHaveValue('sensor.preserved_mapping');
    expect(screen.getByRole('status')).toHaveTextContent('Mappningen är kvar');
    expect(change).not.toHaveBeenCalled();
  });

  it('döljer tidigare värde vid anslutningsfel och återhämtar samma mappning', () => {
    const change = vi.fn();
    const { rerender } = render(picker(catalog, temperature.entityId, change));
    rerender(picker({ ...catalog, issue: 'Sensorlistan kunde inte uppdateras.' }, temperature.entityId, change));
    expect(screen.getByRole('combobox')).toBeDisabled();
    expect(screen.getByRole('combobox')).toHaveValue('Vardagsrum · sensor.room_temperature');
    expect(screen.getByRole('status')).not.toHaveTextContent('68 °F');
    expect(screen.queryByText('Värde/enhet OK')).not.toBeInTheDocument();
    rerender(picker(catalog, temperature.entityId, change));
    expect(screen.getByRole('combobox')).toBeEnabled();
    expect(screen.getByText('Värde/enhet OK')).toBeInTheDocument();
    expect(change).not.toHaveBeenCalled();
  });

  it('kan välja en effektgivare med tangentbord och stödjer W till kW', async () => {
    const change = vi.fn();
    const user = userEvent.setup();
    render(picker(catalog, '', change, 'kW'));
    const input = screen.getByRole('combobox');
    await user.click(input);
    await user.type(input, 'Värmepump');
    const option = screen.getByRole('option', { name: /Värmepump/ });
    expect(option).toHaveTextContent('1500 W');
    expect(option).toHaveTextContent('Värde/enhet OK');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(change).toHaveBeenCalledExactlyOnceWith(power);
  });

  it('behåller ID men tar bort godkännandet när katalogkontrollen åldras', () => {
    const { rerender } = render(picker());
    rerender(picker({ ...catalog, nowUtc: now + 121_000 }));
    expect(screen.getByRole('combobox')).toHaveValue('Vardagsrum · sensor.room_temperature');
    expect(screen.getByText('Gammal')).toBeInTheDocument();
    expect(screen.queryByText('Värde/enhet OK')).not.toBeInTheDocument();
  });

  it('har inga automatiskt identifierade a11y-fel med öppna alternativ eller felaktigt val', async () => {
    const user = userEvent.setup();
    const { container } = render(<main>{picker(catalog, power.entityId)}</main>);
    await user.click(screen.getByRole('combobox'));
    const result = await axe(container.ownerDocument.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });
});
