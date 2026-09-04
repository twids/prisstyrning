import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import SessionGate from './SessionGate';

const mocks = vi.hoisted(() => ({
  startAuth: vi.fn(),
  session: {
    data: { authenticated: false, userId: null, isAdmin: false, csrfToken: 'csrf' } as { authenticated: boolean; userId: string | null; isAdmin: boolean; csrfToken: string | null },
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: vi.fn(),
    error: null as Error | null,
  },
}));

vi.mock('../api/client', () => ({
  apiClient: { startAuth: mocks.startAuth },
}));

vi.mock('../hooks/useSession', () => ({
  useSession: () => mocks.session,
}));

describe('SessionGate', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.session.data = { authenticated: false, userId: null, isAdmin: false, csrfToken: 'csrf' };
    mocks.session.isLoading = false;
    mocks.session.isError = false;
    mocks.session.isFetching = false;
    mocks.session.error = null;
  });

  it('visar inga anläggningsvyer före verifierad inloggning', () => {
    render(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    expect(screen.getByRole('heading', { name: 'Logga in för att fortsätta' })).toBeInTheDocument();
    expect(screen.queryByText('Hemlig anläggningsvy')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Logga in med Daikin' })).toBeEnabled();
  });

  it('släpper igenom innehållet först när sessionen är autentiserad', () => {
    mocks.session.data = { authenticated: true, userId: 'daikin-account', isAdmin: false, csrfToken: 'csrf' };
    render(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    expect(screen.getByText('Hemlig anläggningsvy')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Logga in med Daikin' })).not.toBeInTheDocument();
  });

  it('förklarar startfel utan råa serversvar och låter användaren försöka igen', async () => {
    const user = userEvent.setup();
    mocks.startAuth.mockRejectedValueOnce(new Error('<html>Proxy failure: internal-server-detail</html>'));
    render(<SessionGate><div /></SessionGate>);

    await user.click(screen.getByRole('button', { name: 'Logga in med Daikin' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Inloggningen kunde inte startas. Försök igen om en stund.');
    expect(screen.queryByText(/internal-server-detail/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Logga in med Daikin' })).toBeEnabled();
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });

  it('visar en namngiven väntestatus utan anläggningsdata under första kontrollen', () => {
    mocks.session.isLoading = true;
    render(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    expect(screen.getByRole('main')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Kontrollerar inloggning' })).toBeInTheDocument();
    expect(screen.queryByText('Hemlig anläggningsvy')).not.toBeInTheDocument();
  });

  it('döljer även cachelagrad anläggningsdata vid sessionsfel och kan provas igen med tangentbord', async () => {
    const user = userEvent.setup();
    mocks.session.data.authenticated = true;
    mocks.session.isError = true;
    mocks.session.error = new Error('raw-internal-server-detail');
    render(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    expect(screen.getByRole('heading', { name: 'Inloggningen kunde inte kontrolleras' })).toBeInTheDocument();
    expect(screen.queryByText('Hemlig anläggningsvy')).not.toBeInTheDocument();
    expect(screen.queryByText(/raw-internal-server-detail/)).not.toBeInTheDocument();
    expect(screen.getByText(/Kontrollen ändrar inga inställningar eller scheman/)).toBeInTheDocument();

    await user.tab();
    expect(screen.getByRole('button', { name: 'Försök igen' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(mocks.session.refetch).toHaveBeenCalledTimes(1);
    expect(mocks.startAuth).not.toHaveBeenCalled();
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });

  it('spärrar dubbla återförsök och öppnar först efter ett nytt godkänt sessionssvar', () => {
    mocks.session.isError = true;
    mocks.session.isFetching = true;
    const view = render(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    const retry = screen.getByRole('button', { name: 'Kontrollerar…' });
    expect(retry).toBeDisabled();
    expect(retry).toHaveAttribute('aria-busy', 'true');
    expect(screen.queryByText('Hemlig anläggningsvy')).not.toBeInTheDocument();

    mocks.session.isError = false;
    mocks.session.isFetching = false;
    mocks.session.data = { authenticated: true, userId: 'daikin-account', isAdmin: false, csrfToken: 'new-csrf' };
    view.rerender(<SessionGate><div>Hemlig anläggningsvy</div></SessionGate>);

    expect(screen.getByText('Hemlig anläggningsvy')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Försök igen' })).not.toBeInTheDocument();
  });
});
