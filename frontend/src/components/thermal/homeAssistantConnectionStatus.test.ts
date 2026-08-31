import { describe, expect, it } from 'vitest';
import type { HomeAssistantConnection, HomeAssistantStatus } from '../../types/api';
import { assessHomeAssistantLive } from './homeAssistantConnectionStatus';

const now = Date.now();
const connection: HomeAssistantConnection = {
  baseUrl: 'https://ha.example.test', telemetryEnabled: true, controlEnabled: false, heatingDeviationEntityId: '',
  telemetryTokenConfigured: true, controlTokenConfigured: false, staleAfterMinutes: 10, updatedAtUtc: new Date(now - 60_000).toISOString(),
};
const status: HomeAssistantStatus = {
  configured: true, connected: true, phase: 'Connected', configurationUpdatedAtUtc: connection.updatedAtUtc,
  lastSnapshotUtc: new Date(now - 30_000).toISOString(), lastActivityUtc: new Date(now).toISOString(), cachedEntities: 20,
};
const assess = (value: HomeAssistantStatus = status, saved: HomeAssistantConnection | null = connection, checkedAt = now) =>
  assessHomeAssistantLive(saved, value, checkedAt, now);

describe('HA liveanslutning', () => {
  it('kräver rätt revision, bekräftad prenumeration och en senare startbild', () => {
    expect(assess()).toMatchObject({ label: 'Liveansluten', verified: true, showDiagnostics: true, severity: 'success' });
  });

  it.each([
    ['Reloading', 'Laddar om anslutningen'], ['Connecting', 'Ansluter till Home Assistant'],
    ['Synchronizing', 'Läser ny startbild'], ['Reconnecting', 'Återansluter automatiskt'], ['Disconnected', 'Liveanslutningen är bruten'],
  ] as const)('förklarar %s utan grönt godkännande', (phase, label) => {
    expect(assess({ ...status, phase, connected: false })).toMatchObject({ label, verified: false });
    expect(assess({ ...status, phase, connected: false }).severity).not.toBe('success');
  });

  it.each([
    { configurationUpdatedAtUtc: undefined }, { configurationUpdatedAtUtc: new Date(now - 120_000).toISOString() },
  ])('döljer cache från annan eller okänd revision: %j', change => {
    expect(assess({ ...status, ...change })).toMatchObject({ label: 'Kontrollerar sparad ändring', verified: false, showDiagnostics: false });
  });

  it('jämför hela revisionen utan att avrunda bort mikrosekunder', () => {
    const saved = { ...connection, updatedAtUtc: '2026-08-31T04:00:00.123456+00:00' };
    expect(assess({ ...status, configurationUpdatedAtUtc: '2026-08-31T04:00:00.123455+00:00' }, saved).verified).toBe(false);
  });

  it.each([null, 'invalid', new Date(now - 120_000).toISOString(), new Date(now + 60_000).toISOString()])('underkänner startbild %s', lastSnapshotUtc => {
    expect(assess({ ...status, lastSnapshotUtc })).toMatchObject({ label: 'Startbild inte verifierad', verified: false, showDiagnostics: false });
  });

  it.each([0, NaN, now - 120_001, now + 30_001])('döljer gammalt eller felaktigt statusbesked %s', checkedAt => {
    expect(assess(status, connection, checkedAt)).toMatchObject({ label: 'Statusen är gammal', verified: false, showDiagnostics: false });
  });

  it('behåller ingen grön status vid läsfel eller laddning', () => {
    expect(assessHomeAssistantLive(connection, status, now, now, false, true)).toMatchObject({ label: 'Status kunde inte hämtas', verified: false, showDiagnostics: false });
    expect(assessHomeAssistantLive(connection, status, now, now, true)).toMatchObject({ label: 'Läser anslutningsstatus', verified: false });
  });

  it('skiljer saknad anslutning, inaktivering och saknad token', () => {
    expect(assess(status, null).label).toBe('Ingen anslutning sparad');
    expect(assess(status, { ...connection, telemetryEnabled: false }).label).toBe('Telemetri avstängd');
    expect(assess(status, { ...connection, telemetryTokenConfigured: false }).label).toBe('Telemetritoken saknas');
  });

  it('återger inte okända servervärden och gissar inte ansluten', () => {
    const result = assess({ ...status, phase: 'private-server-detail' as HomeAssistantStatus['phase'] });
    expect(result.verified).toBe(false);
    expect(result.showDiagnostics).toBe(false);
    expect(JSON.stringify(result)).not.toContain('private-server-detail');
    expect(assess({ ...status, phase: undefined }).verified).toBe(false);
  });
});
