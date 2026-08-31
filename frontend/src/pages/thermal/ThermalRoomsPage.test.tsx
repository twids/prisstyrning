import { act, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ThermalRoomConfig } from '../../types/api';
import ThermalRoomsPage from './ThermalRoomsPage';

const hooks = vi.hoisted(() => ({ config: vi.fn(), history: vi.fn(), events: vi.fn() }));
vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalConfig: hooks.config,
  useThermalHistory: hooks.history,
  useThermalEvents: hooks.events,
}));

const now = Date.parse('2026-08-31T00:45:00Z');
const room: ThermalRoomConfig = {
  id: 1, userId: 'test', name: 'Vardagsrum', entityId: 'sensor.vardagsrum_temperature',
  targetOffsetC: 0, weight: 2, isCritical: true, enabled: true,
  minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3,
};
const config = { site: { baseRoomTargetC: 21.5, lowerComfortBandC: .5, upperComfortBandC: .7 }, rooms: [room] };
const sample = {
  timestampUtc: new Date(now - 60_000).toISOString(),
  roomTemperaturesJson: JSON.stringify({ [room.entityId]: 21.2 }),
  qualityJson: JSON.stringify({ rooms: { [room.entityId]: { Quality: 0, Excluded: false, Reason: null } } }),
};

function renderRooms() {
  return render(<MemoryRouter><main><ThermalRoomsPage /></main></MemoryRouter>);
}

describe('ThermalRoomsPage', () => {
  beforeEach(() => {
    vi.spyOn(Date, 'now').mockReturnValue(now);
    hooks.config.mockReturnValue({ data: config, isLoading: false, isError: false });
    hooks.history.mockReturnValue({ data: [sample], isLoading: false, isError: false });
    hooks.events.mockReturnValue({ data: [], isLoading: false, isError: false });
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('visar återhämtning som daterad information i historiken, inte som ett aktivt larm', () => {
    hooks.events.mockReturnValue({ data: [{
      id: 1, timestampUtc: new Date(now - 3_600_000).toISOString(), severity: 'Information',
      category: 'DataQuality', message: 'Givaren används igen efter tre giltiga mätningar.', detailsJson: '{}',
    }], isLoading: false, isError: false });
    renderRooms();

    const history = screen.getByRole('region', { name: 'Rum- och givarhistorik' });
    expect(within(history).getByText('Information')).toBeInTheDocument();
    expect(within(history).getByText(/inte en lista över aktiva larm/)).toBeInTheDocument();
    expect(within(history).getByText('Givaren används igen efter tre giltiga mätningar.')).toBeInTheDocument();
    expect(history.querySelector('time')).toHaveAttribute('datetime', '2026-08-30T23:45:00.000Z');
    expect(within(history).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('märker ett exkluderat kritiskt rums reservvärde utan att intyga rummets komfort', () => {
    hooks.history.mockReturnValue({ data: [{ ...sample,
      qualityJson: JSON.stringify({ rooms: { [room.entityId]: { Quality: 2, Excluded: true, Reason: 'Värdet ligger utanför tillåtet intervall.' } } }),
    }], isLoading: false, isError: false });
    renderRooms();

    expect(screen.getByText('Sparat reservvärde')).toBeInTheDocument();
    expect(screen.getByText('Exkluderad')).toBeInTheDocument();
    expect(screen.getByText('Okänd')).toBeInTheDocument();
    expect(screen.queryByText(/Giltig ·/)).not.toBeInTheDocument();
  });

  it('beräknar inte aktuell komfortmarginal från en gammal mätning', () => {
    hooks.history.mockReturnValue({ data: [{ ...sample, timestampUtc: new Date(now - 11 * 60_000).toISOString() }], isLoading: false, isError: false });
    renderRooms();

    expect(screen.getByText('Gammal')).toBeInTheDocument();
    expect(screen.getByText('Okänd')).toBeInTheDocument();
  });

  it('behåller komfortskyddet och en negativ marginal för ett giltigt kallt rum', () => {
    hooks.history.mockReturnValue({ data: [{ ...sample, roomTemperaturesJson: JSON.stringify({ [room.entityId]: 20.8 }) }], isError: false });
    renderRooms();

    expect(screen.getByText('Giltig')).toBeInTheDocument();
    expect(screen.getByText('−0,2 °C')).toBeInTheDocument();
    expect(screen.getByText(/Under komfortgränsen/)).toBeInTheDocument();
    expect(screen.getByText(/1 av 1 aktiverade rum/)).toBeInTheDocument();
  });

  it('skiljer samtliga registrerade larmnivåer med text och behåller äldre varningar', () => {
    hooks.events.mockReturnValue({ data: [
      { id: 1, timestampUtc: '2026-08-30T21:00:00Z', severity: 'Warning', category: 'RoomBalance', message: 'Kontrollera injusteringen.' },
      { id: 2, timestampUtc: '2026-08-30T22:00:00Z', severity: 'ActionRequired', category: 'DataQuality', message: 'Givaren exkluderades.' },
      { id: 3, timestampUtc: '2026-08-30T23:00:00Z', severity: 'Information', category: 'DataQuality', message: 'Givaren återhämtade sig.' },
    ], isError: false });
    renderRooms();

    const history = screen.getByRole('region', { name: 'Rum- och givarhistorik' });
    const entries = within(history).getAllByRole('listitem');
    expect(entries).toHaveLength(3);
    expect(entries[0]).toHaveTextContent('Information');
    expect(entries[1]).toHaveTextContent('Åtgärd krävs');
    expect(entries[2]).toHaveTextContent('Varning');
    expect(within(history).queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('Giltig')).toBeInTheDocument();
    expect(within(history).getByRole('link', { name: 'Öppna hela händelseloggen' })).toHaveAttribute('href', '/events');
  });

  it('gör cachade värden overifierade vid hämtningsfel utan att visa råa serverfel', () => {
    hooks.history.mockReturnValue({ data: [sample], isError: true, error: new Error('private-server-detail') });
    renderRooms();

    expect(screen.getByRole('alert')).toHaveTextContent('Mätvärdena kunde inte hämtas.');
    expect(screen.getByText('Kan inte verifieras')).toBeInTheDocument();
    expect(screen.getByText('Okänd')).toBeInTheDocument();
    expect(screen.queryByText('Senaste giltiga mätvärde')).not.toBeInTheDocument();
    expect(screen.queryByText(/private-server-detail/)).not.toBeInTheDocument();
  });

  it('kallar inte en misslyckad historikhämtning för tom eller åtgärdad historik', () => {
    hooks.events.mockReturnValue({ data: [], isLoading: false, isError: true });
    renderRooms();

    expect(screen.getByRole('alert')).toHaveTextContent('Det betyder inte att tidigare varningar är åtgärdade.');
    expect(screen.queryByText(/Inga rum- eller givarhändelser/)).not.toBeInTheDocument();
  });

  it('märker importerade värden som historik och räknar bort inaktiverade rum', () => {
    hooks.config.mockReturnValue({ data: { ...config, rooms: [room, { ...room, id: 2, name: 'Pausat rum', entityId: 'sensor.paused', enabled: false }] } });
    hooks.history.mockReturnValue({ data: [{ ...sample, qualityJson: JSON.stringify({ source: 'HomeAssistantHistoryImport', rooms: { [room.entityId]: { quality: 0, excluded: false } } }) }] });
    renderRooms();

    expect(screen.getByText('Importerad historik')).toBeInTheDocument();
    expect(screen.getByText('Inaktiverad')).toBeInTheDocument();
    expect(screen.getByText(/0 av 1 aktiverade rum/)).toBeInTheDocument();
    expect(screen.queryByText('Giltig')).not.toBeInTheDocument();
  });

  it('låter mätvärdet bli gammalt utan att invänta en lyckad ny hämtning', () => {
    vi.restoreAllMocks();
    vi.useFakeTimers();
    vi.setSystemTime(now);
    hooks.history.mockReturnValue({ data: [{ ...sample, timestampUtc: new Date(now - 10 * 60_000 + 1_000).toISOString() }] });
    renderRooms();
    expect(screen.getByText('Giltig')).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(30_000));

    expect(screen.getByText('Gammal')).toBeInTheDocument();
    expect(screen.getByText('Okänd')).toBeInTheDocument();
  });

  it('hanterar trasig JSON och ogiltiga händelsetider utan att krascha', () => {
    hooks.history.mockReturnValue({ data: [{ ...sample, qualityJson: 'null', roomTemperaturesJson: 'null' }] });
    hooks.events.mockReturnValue({ data: [{ id: 1, severity: 'Information', category: 'DataQuality', message: 'En äldre händelse.', timestampUtc: 'invalid' }] });
    renderRooms();

    expect(screen.getByText('Status okänd')).toBeInTheDocument();
    expect(screen.getByText(/Okänd tid/)).toBeInTheDocument();
  });

  it('har tydliga rubriker, semantiska listor och inga automatiskt identifierade tillgänglighetsfel', async () => {
    hooks.events.mockReturnValue({ data: [{ id: 1, severity: 'Warning', category: 'RoomBalance', message: 'En registrerad varning.', timestampUtc: sample.timestampUtc }] });
    renderRooms();

    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations.map(violation => ({ id: violation.id, nodes: violation.nodes.map(node => node.failureSummary) }))).toEqual([]);
  });
});
