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

  it('förklarar startfel och har inga automatiskt identifierade tillgänglighetsfel', async () => {
    const user = userEvent.setup();
    mocks.startAuth.mockRejectedValueOnce(new Error('ONECTA kunde inte nås'));
    render(<SessionGate><div /></SessionGate>);

    await user.click(screen.getByRole('button', { name: 'Logga in med Daikin' }));
    expect(await screen.findByText('ONECTA kunde inte nås')).toBeInTheDocument();
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });
});
