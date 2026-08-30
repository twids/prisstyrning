import { beforeEach, describe, expect, it, vi } from 'vitest';

describe('ApiClient säkerhetskontrakt', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.unstubAllGlobals();
  });

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
});
