import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ConservativeStartWizard from './ConservativeStartWizard';

const hooks = vi.hoisted(() => ({ status: vi.fn(), start: vi.fn(), submit: vi.fn(), refetch: vi.fn() }));
vi.mock('../../hooks/thermal/useConservativeStartup', () => ({ useConservativeStartup: hooks.status, useStartConservativeHeating: hooks.start }));

function result() {
  return { data: { readyToCommission: true, phase: 'NotCommissioned', conservativeEnabled: false,
    safetyChecks: [{ key: 'live', requirement: 'Verifierad telemetri', passed: true, action: 'Godkänt.' }], optimizationChecks: [] },
    dataUpdatedAt: Date.now(), isLoading: false, isFetching: false, isError: false, refetch: hooks.refetch };
}
async function open() {
  const user = userEvent.setup();
  render(<ConservativeStartWizard />);
  await user.click(screen.getByRole('button', { name: 'Försiktig start och inlärning' }));
  return user;
}
describe('försiktig start', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    hooks.status.mockReturnValue(result());
    hooks.submit.mockResolvedValue({});
    hooks.start.mockReturnValue({ mutateAsync: hooks.submit, isPending: false, isError: false, isSuccess: false });
  });

  it('requires all three explicit confirmations before a real write test', async () => {
    const user = await open();
    const button = screen.getByRole('button', { name: 'Verifiera inkoppling och starta' });
    expect(button).toBeDisabled();
    const checks = screen.getAllByRole('checkbox');
    await user.click(checks[0]);
    await user.click(checks[1]);
    expect(button).toBeDisabled();
    await user.click(checks[2]);
    expect(button).toBeEnabled();
    await user.click(button);
    expect(hooks.submit).toHaveBeenCalledTimes(1);
  });

  it.each(['stale', 'failed', 'empty', 'unsafe', 'fetching'])('fails closed for %s readiness', async fault => {
    const status = result();
    if (fault === 'stale') status.dataUpdatedAt = Date.now() - 120_000;
    if (fault === 'failed') status.isError = true;
    if (fault === 'empty') status.data.safetyChecks = [];
    if (fault === 'unsafe') status.data.safetyChecks[0].passed = false;
    if (fault === 'fetching') status.isFetching = true;
    hooks.status.mockReturnValue(status);
    const user = await open();
    for (const check of screen.getAllByRole('checkbox')) await user.click(check);
    expect(screen.getByRole('button', { name: 'Verifiera inkoppling och starta' })).toBeDisabled();
    expect(hooks.submit).not.toHaveBeenCalled();
  });

  it('explains the physical test and separates maturity without promising optimality', async () => {
    await open();
    expect(screen.getByText(/inte ett skrivfritt Shadow-test/)).toBeInTheDocument();
    expect(screen.getByText(/Tre veckor är en utvärderingspunkt/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: /Modellens mognad/ })).toBeInTheDocument();
    expect((await axe(screen.getByRole('dialog'))).violations).toEqual([]);
  });

  it('does not hide a failure behind a success message', async () => {
    hooks.start.mockReturnValue({ mutateAsync: hooks.submit, isPending: false, isError: true, isSuccess: false });
    await open();
    expect(screen.getByText(/Starten kunde inte bekräftas/)).toBeInTheDocument();
    expect(screen.queryByText(/Inkoppling och nollställning verifierades/)).not.toBeInTheDocument();
  });
});
