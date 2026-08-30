import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { useHomeAssistant } from '../../hooks/thermal/useThermal';
import { HomeAssistantConnectionPanel } from './ThermalSettingsPage';

const save = vi.fn();
const remove = vi.fn();
const testConnection = vi.fn();
const importHistory = vi.fn();

function homeAssistantHook(): ReturnType<typeof useHomeAssistant> {
  return {
    config: { data: null, isLoading: false, isError: false, error: null },
    status: { data: { configured: false, connected: false, lastSnapshotUtc: null, lastActivityUtc: null, cachedEntities: 0 } },
    entities: { data: [] },
    test: { mutate: testConnection, isPending: false, isSuccess: false, isError: false, error: null },
    save: { mutate: save, isPending: false, isSuccess: false, isError: false, error: null },
    remove: { mutate: remove, isPending: false },
    importHistory: { mutate: importHistory, isPending: false, isSuccess: false, isError: false, error: null },
  } as unknown as ReturnType<typeof useHomeAssistant>;
}

describe('HomeAssistantConnectionPanel', () => {
  beforeEach(() => vi.clearAllMocks());

  it('sparar en ny HA-anslutning på kontot utan containerinställningar', async () => {
    const user = userEvent.setup();
    render(<HomeAssistantConnectionPanel ha={homeAssistantHook()} connection={null} />);

    expect(screen.getByText('Ditt kontos Home Assistant')).toBeInTheDocument();
    expect(screen.queryByText(/Docker-secret|containerkonfiguration/i)).not.toBeInTheDocument();
    const saveButton = screen.getByRole('button', { name: 'Spara HA-anslutning' });
    expect(saveButton).toBeDisabled();

    await user.type(screen.getByLabelText(/Home Assistant-adress/i), 'https://ha.example.se');
    const telemetryToken = screen.getByLabelText(/^Telemetritoken$/i);
    expect(telemetryToken).toHaveAttribute('type', 'password');
    await user.type(telemetryToken, 'read-only-token');
    expect(saveButton).toBeEnabled();
    await user.click(saveButton);

    expect(save).toHaveBeenCalledWith(expect.objectContaining({
      baseUrl: 'https://ha.example.se',
      telemetryToken: 'read-only-token',
      telemetryEnabled: true,
      controlEnabled: false,
    }));
  });

  it('har inga automatiskt identifierade tillgänglighetsfel i anslutningsformuläret', async () => {
    render(<main><HomeAssistantConnectionPanel ha={homeAssistantHook()} connection={null} /></main>);
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });
});
