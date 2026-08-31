import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ControlMode } from '../types/api';

describe('ApiClient säkerhetskontrakt', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });
  afterEach(() => vi.unstubAllGlobals());

  it('hämtar sessionens CSRF-token och skickar den på kontobundna mutationer', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ authenticated: true, userId: 'account-a', isAdmin: false, csrfToken: 'csrf-value' }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ baseUrl: 'https://ha.example.se' }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    vi.stubGlobal('fetch', fetchMock);
    const { apiClient } = await import('./client');

    await apiClient.saveHomeAssistantConfig({
      baseUrl: 'https://ha.example.se',
      telemetryToken: 'secret',
      controlToken: null,
      telemetryEnabled: true,
      controlEnabled: false,
      heatingDeviationEntityId: '',
      staleAfterMinutes: 10,
      clearControlToken: false,
    });

    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/session', expect.objectContaining({ credentials: 'same-origin' }));
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/home-assistant/config', expect.objectContaining({
      method: 'PUT',
      credentials: 'same-origin',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-value' }),
    }));
  });

  it.each([
    [0, 0, 0, 'Legacy', 'Legacy', 'Valid'],
    [1, 0, 1, 'Shadow', 'Legacy', 'Stale'],
    [2, 0, 2, 'LwtActive', 'Legacy', 'Invalid'],
    [3, 1, 3, 'FullActive', 'Joint', 'Unavailable'],
    ['Shadow', 'Legacy', 'Valid', 'Shadow', 'Legacy', 'Valid'],
  ])('översätter statuskontraktet %s/%s/%s utan att ändra serverns format', async (mode, writer, quality, expectedMode, expectedWriter, expectedQuality) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({
      mode, dhwWriter: writer, overallDataQuality: quality, dataQualityReason: 'Testad kvalitetsförklaring.',
      currentLwtDeviationC: 0, lastTelemetryUtc: '2026-08-31T02:00:00Z',
    })));
    const { apiClient } = await import('./client');
    expect(await apiClient.getThermalStatus()).toMatchObject({
      mode: expectedMode, dhwWriter: expectedWriter, overallDataQuality: expectedQuality,
      dataQualityReason: 'Testad kvalitetsförklaring.', currentLwtDeviationC: 0,
    });
  });

  it.each([
    { mode: 9 }, { mode: -1 }, { mode: 0.5 }, { mode: '0' }, { mode: null },
    { dhwWriter: 2 }, { dhwWriter: 'unknown-value' },
    { overallDataQuality: 99 }, { overallDataQuality: undefined }, { overallDataQuality: 'unknown-value' },
  ])('avvisar okända statusvärden och gissar aldrig Giltig eller Legacy: %j', async invalid => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ mode: 0, dhwWriter: 0, overallDataQuality: 0, ...invalid })));
    const { apiClient } = await import('./client');
    await expect(apiClient.getThermalStatus()).rejects.toThrow('kunde inte tolkas');
    // Error text deliberately does not reflect the raw, potentially sensitive value.
  });

  it.each<[{ mode: ControlMode; numeric: number }]>([
    [{ mode: 'Legacy', numeric: 0 }], [{ mode: 'Shadow', numeric: 1 }],
    [{ mode: 'LwtActive', numeric: 2 }], [{ mode: 'FullActive', numeric: 3 }],
  ])('skickar ett avsiktligt lägesbyte som numeriskt JSON med CSRF: %j', async ({ mode, numeric }) => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ csrfToken: 'test-csrf' }))
      .mockResolvedValueOnce(jsonResponse({ message: 'Test only' }));
    vi.stubGlobal('fetch', fetchMock);
    const { apiClient } = await import('./client');

    await apiClient.changeThermalMode(mode);

    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/thermal/mode', expect.objectContaining({
      method: 'POST', headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'test-csrf' }),
      body: JSON.stringify({ mode: numeric, confirmed: true }), credentials: 'same-origin',
    }));
  });

  it('skickar ingenting om klientens önskade driftläge är okänt', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const { apiClient } = await import('./client');
    await expect(apiClient.changeThermalMode('unexpected' as ControlMode)).rejects.toThrow('kunde inte tolkas');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('översätter även readiness och entity-listans enumvärden', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(jsonResponse({ targetMode: 3, ready: false, checks: [] }))
      .mockResolvedValueOnce(jsonResponse([{ entityId: 'sensor.test', quality: 1 }, { entityId: 'sensor.missing', quality: 3 }])));
    const { apiClient } = await import('./client');
    expect(await apiClient.getThermalReadiness('FullActive')).toMatchObject({ targetMode: 'FullActive', ready: false });
    expect(await apiClient.getHomeAssistantEntities()).toEqual([
      { entityId: 'sensor.test', quality: 'Stale' }, { entityId: 'sensor.missing', quality: 'Unavailable' },
    ]);
  });
});

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } });
}
