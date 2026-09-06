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
const previewHistory = vi.fn();

function homeAssistantHook(): ReturnType<typeof useHomeAssistant> {
  return {
    config: { data: null, isLoading: false, isError: false, error: null },
    status: { data: { configured: false, connected: false, lastSnapshotUtc: null, lastActivityUtc: null, cachedEntities: 0 } },
    entities: { data: [] },
    test: { mutate: testConnection, isPending: false, isSuccess: false, isError: false, error: null },
    save: { mutate: save, isPending: false, isSuccess: false, isError: false, error: null },
    remove: { mutate: remove, isPending: false },
    importHistory: { mutate: importHistory, isPending: false, isSuccess: false, isError: false, error: null },
    previewHistory: { mutate: previewHistory, reset: vi.fn(), isPending: false, isSuccess: false, isError: false, error: null },
  } as unknown as ReturnType<typeof useHomeAssistant>;
}

describe('HomeAssistantConnectionPanel', () => {
  beforeEach(() => vi.clearAllMocks());

  it('kontrollerar sparad historik utan import, träning eller inställningsskrivning', async () => {
    const ha = homeAssistantHook();
    ha.status.data!.configured = true;
    render(<HomeAssistantConnectionPanel ha={ha} connection={null} />);
    await userEvent.setup().click(screen.getByRole('button', { name: 'Kontrollera historik' }));
    expect(previewHistory).toHaveBeenCalledOnce();
    expect(importHistory).not.toHaveBeenCalled();
    expect(save).not.toHaveBeenCalled();
  });

  it('visar datakvalitet per givare utan att utlova en godkänd modell och återanvänder inte gamla resultat efter ommontering', async () => {
    const ha = homeAssistantHook();
    ha.status.data!.configured = true;
    Object.assign(ha.previewHistory, { isSuccess: true, data: { expectedSamples: 10, existingSamples: 2,
      sensors: [{ entityId: 'sensor.room', purpose: 'Rum: Vardagsrum', valid: 3, stale: 7, invalid: 0, unavailable: 0,
        timelineIssue: 'Kontrollera källans tidsstämplar.' }] } });
    const view = render(<HomeAssistantConnectionPanel key="first-connection" ha={ha} connection={null} />);
    expect(screen.queryByText(/Giltiga: 3. Osäker rapportålder: 7/)).not.toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Kontrollera historik' }));
    expect(screen.getByText(/Giltiga: 3. Osäker rapportålder: 7/)).toBeInTheDocument();
    expect(screen.getByText(/Ingen modell eller Shadow-period är godkänd/)).toBeInTheDocument();
    expect(screen.getByText('Kontrollera källans tidsstämplar.')).toBeInTheDocument();
    view.rerender(<HomeAssistantConnectionPanel key="changed-connection" ha={ha} connection={null} />);
    expect(screen.queryByText(/Giltiga: 3. Osäker rapportålder: 7/)).not.toBeInTheDocument();
  });

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
