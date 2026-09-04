import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Layout from './Layout';

const mocks = vi.hoisted(() => ({ logout: { mutate: vi.fn(), isPending: false, isError: false, error: null as Error | null } }));
vi.mock('../hooks/useSession', () => ({ useLogout: () => mocks.logout }));
vi.mock('@tanstack/react-query', () => ({ useQuery: () => ({ data: { isAdmin: false } }) }));
vi.mock('./thermal/ThermalStatusStrip', () => ({ default: () => <section aria-label="Styrsystemets status">Driftstatus</section> }));

const content = <MemoryRouter><Layout><h1>Testvy</h1></Layout></MemoryRouter>;

describe('Layout utloggning', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.logout.isPending = false;
    mocks.logout.isError = false;
    mocks.logout.error = null;
  });

  it('visar inget utloggningslarm före ett fel', () => {
    render(content);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Logga ut/ })).toBeEnabled();
  });

  it('förklarar osäker utloggning utan interna fel och erbjuder ett nytt försök', async () => {
    const user = userEvent.setup();
    mocks.logout.isError = true;
    mocks.logout.error = new Error('internal-logout-server-detail');
    render(content);

    expect(screen.getByRole('alert')).toHaveTextContent('Du kan fortfarande vara inloggad.');
    expect(screen.queryByText(/internal-logout-server-detail/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /^Logga ut/ }));
    expect(mocks.logout.mutate).toHaveBeenCalledTimes(1);
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations.map(violation => ({
      id: violation.id,
      nodes: violation.nodes.map(node => ({ target: node.target, summary: node.failureSummary })),
    }))).toEqual([]);
  });

  it('spärrar nya klick medan utloggningen väntar på svar', () => {
    mocks.logout.isPending = true;
    render(content);

    expect(screen.getByRole('button', { name: /^Logga ut/ })).toBeDisabled();
    expect(screen.getByText('Loggar ut…')).toBeInTheDocument();
    expect(mocks.logout.mutate).not.toHaveBeenCalled();
  });
});
