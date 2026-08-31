import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { HomeAssistantConnection, HomeAssistantStatus } from '../../types/api';
import HomeAssistantLiveStatus from './HomeAssistantLiveStatus';

const now = Date.now();
const connection: HomeAssistantConnection = {
  baseUrl: 'https://ha.example.test', telemetryEnabled: true, controlEnabled: false, heatingDeviationEntityId: '',
  telemetryTokenConfigured: true, controlTokenConfigured: false, staleAfterMinutes: 10, updatedAtUtc: new Date(now - 60_000).toISOString(),
};
const status: HomeAssistantStatus = {
  configured: true, connected: true, phase: 'Connected', configurationUpdatedAtUtc: connection.updatedAtUtc,
  lastSnapshotUtc: new Date(now - 30_000).toISOString(), lastActivityUtc: new Date(now).toISOString(), cachedEntities: 25,
};
const props = { connection, status, checkedAt: now, loading: false, error: false, refreshing: false, refresh: vi.fn() };

describe('HA livekort', () => {
  afterEach(() => { vi.useRealTimers(); vi.clearAllMocks(); });

  it('visar tydlig verifiering med namngiven region och läsbara mätvärden', async () => {
    render(<main><HomeAssistantLiveStatus {...props} /></main>);
    const region = screen.getByRole('region', { name: 'Liveanslutning' });
    expect(within(region).getByRole('status')).toHaveTextContent('Liveansluten');
    expect(within(region).getByText('25')).toBeVisible();
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });

  it('döljer gamla cachevärden vid läsfel och låter tangentbordet hämta status', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<HomeAssistantLiveStatus {...props} />);
    rerender(<HomeAssistantLiveStatus {...props} error />);
    expect(screen.getByRole('status')).toHaveTextContent('Status kunde inte hämtas');
    expect(screen.queryByText('Liveansluten')).not.toBeInTheDocument();
    expect(screen.queryByText('25')).not.toBeInTheDocument();
    const retry = screen.getByRole('button', { name: 'Uppdatera anslutningsstatus' });
    retry.focus();
    await user.keyboard('{Enter}');
    expect(props.refresh).toHaveBeenCalledTimes(1);
    rerender(<HomeAssistantLiveStatus {...props} />);
    expect(screen.getByRole('status')).toHaveTextContent('Liveansluten');
  });

  it('håller gamla statusbesked dolda efter sparad ändring', () => {
    render(<HomeAssistantLiveStatus {...props} connection={{ ...connection, updatedAtUtc: new Date(now).toISOString() }} />);
    expect(screen.getByRole('status')).toHaveTextContent('Kontrollerar sparad ändring');
    expect(screen.queryByText('25')).not.toBeInTheDocument();
    expect(screen.queryByText('Liveansluten')).not.toBeInTheDocument();
  });

  it('åldrar godkännandet även när polling inte har levererat ett nytt svar', () => {
    vi.useFakeTimers();
    vi.setSystemTime(now);
    render(<HomeAssistantLiveStatus {...props} />);
    expect(screen.getByRole('status')).toHaveTextContent('Liveansluten');
    act(() => vi.advanceTimersByTime(150_000));
    expect(screen.getByRole('status')).toHaveTextContent('Statusen är gammal');
    expect(screen.queryByText('25')).not.toBeInTheDocument();
  });
});
