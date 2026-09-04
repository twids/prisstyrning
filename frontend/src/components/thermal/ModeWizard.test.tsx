import { act, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ThermalReadiness } from '../../types/api';
import ModeWizard from './ModeWizard';

const mocks = vi.hoisted(() => ({
  reset: vi.fn(),
  mutateAsync: vi.fn(),
  mutationError: false,
  refetch: vi.fn(),
  readiness: {
    data: {
      targetMode: 'LwtActive',
      ready: false,
      checks: [
        { key: 'telemetry', requirement: 'Färsk telemetri', passed: true, action: 'Ingen åtgärd krävs.', severity: 'Information' as const },
        { key: 'weather', requirement: 'Verifierad grundkurva', passed: false, action: 'Slutför grundkurvetestet.', severity: 'ActionRequired' as const },
      ],
    } as ThermalReadiness,
    dataUpdatedAt: Date.now(),
    isLoading: false,
    isFetching: false,
    isError: false,
    error: null as Error | null,
  },
}));

vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalReadiness: () => ({ ...mocks.readiness, refetch: mocks.refetch }),
  useChangeThermalMode: () => ({
    mutateAsync: mocks.mutateAsync,
    reset: mocks.reset,
    isPending: false,
    isError: mocks.mutationError,
    error: null,
  }),
}));

describe('ModeWizard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.mutationError = false;
    mocks.readiness.isError = false;
    mocks.readiness.isLoading = false;
    mocks.readiness.isFetching = false;
    mocks.readiness.error = null;
    mocks.readiness.dataUpdatedAt = Date.now();
    mocks.readiness.data.targetMode = 'LwtActive';
    mocks.readiness.data.ready = false;
    mocks.readiness.data.checks[1].passed = false;
  });
  afterEach(() => vi.useRealTimers());

  it('förhindrar hopp över lägen och blockerar aktivering när readiness saknas', async () => {
    const user = userEvent.setup();
    render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);

    expect(screen.getByRole('radio', { name: /Fullt aktiv/i })).toBeDisabled();
    expect(screen.getByRole('radio', { name: /LWT aktiv/i })).toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));

    expect(screen.getByText('1 av 2 krav är godkända.')).toBeInTheDocument();
    expect(screen.getByText('Slutför grundkurvetestet.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('har inga automatiskt identifierade tillgänglighetsfel i startsteget', async () => {
    render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    const result = await axe(document.body, { rules: { 'color-contrast': { enabled: false } } });
    expect(result.violations).toHaveLength(0);
  });

  it('döljer gamla godkännanden vid läsfel och erbjuder tangentbordsåtkomlig läsåterhämtning', async () => {
    const user = userEvent.setup();
    mocks.readiness.isError = true;
    mocks.readiness.error = new Error('private-server-detail');
    mocks.readiness.data.ready = true;
    mocks.readiness.data.checks[1].passed = true;
    render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));

    expect(screen.getByText(/Kraven kunde inte kontrolleras/)).toBeInTheDocument();
    expect(screen.queryByText(/krav är godkända/)).not.toBeInTheDocument();
    expect(screen.queryByText(/private-server-detail/)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
    screen.getByRole('button', { name: 'Kontrollera kraven igen' }).focus();
    await user.keyboard('{Enter}');
    expect(mocks.refetch).toHaveBeenCalledOnce();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
    expect((await axe(document.body, { rules: { 'color-contrast': { enabled: false } } })).violations).toHaveLength(0);
  });

  it.each(['older-result', 'wrong-mode', 'inconsistent-checks', 'fetching'])('blockerar %s även om ready är sant', async fault => {
    const user = userEvent.setup();
    mocks.readiness.data.ready = true;
    mocks.readiness.data.checks[1].passed = fault !== 'inconsistent-checks';
    if (fault === 'older-result') mocks.readiness.dataUpdatedAt -= 121_000;
    if (fault === 'wrong-mode') mocks.readiness.data.targetMode = 'FullActive';
    if (fault === 'fetching') mocks.readiness.isFetching = true;
    render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    expect(screen.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('drar tillbaka godkännandet i sista steget när telemetrikontrollen misslyckas', async () => {
    const user = userEvent.setup();
    mocks.readiness.data.ready = true;
    mocks.readiness.data.checks[1].passed = true;
    const { rerender } = render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    expect(screen.getByRole('button', { name: 'Aktivera LWT aktiv' })).toBeEnabled();
    mocks.readiness.isError = true;
    rerender(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Aktivera LWT aktiv' })).toBeDisabled();
    expect(screen.getByText(/Kraven kunde inte kontrolleras/)).toBeInTheDocument();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('åldrar ett godkännande även om ingen ny pollning levererar', () => {
    vi.useFakeTimers();
    mocks.readiness.data.ready = true;
    mocks.readiness.data.checks[1].passed = true;
    render(<ModeWizard open currentMode="Shadow" onClose={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Fortsätt' }));
    expect(screen.getByRole('button', { name: 'Fortsätt' })).toBeEnabled();
    act(() => vi.advanceTimersByTime(130_000));
    expect(screen.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
    expect(screen.queryByText(/krav är godkända/)).not.toBeInTheDocument();
    expect(mocks.mutateAsync).not.toHaveBeenCalled();
  });

  it('behåller guidad rollback till Legacy vid telemetrifel och förklarar skrivansvaret', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    mocks.readiness.isError = true;
    mocks.mutateAsync.mockResolvedValueOnce({ success: true });
    render(<ModeWizard open currentMode="LwtActive" onClose={onClose} />);
    await user.click(screen.getByRole('radio', { name: /Legacy.*beprövade/i }));
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    expect(screen.getByText(/Återgång till Legacy kräver inte godkänd telemetri/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    expect(screen.getByText(/Om nollställningen inte kan verifieras avbryter servern/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Aktivera Legacy' }));
    expect(mocks.mutateAsync).toHaveBeenCalledExactlyOnceWith('Legacy');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('fångar nekade lägesbyten utan råa feltexter eller falskt lyckat besked', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    mocks.readiness.data.ready = true;
    mocks.readiness.data.checks[1].passed = true;
    mocks.mutateAsync.mockRejectedValueOnce(new Error('private-response'));
    const { rerender } = render(<ModeWizard open currentMode="Shadow" onClose={onClose} />);
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    await user.click(screen.getByRole('button', { name: 'Fortsätt' }));
    await user.click(screen.getByRole('button', { name: 'Aktivera LWT aktiv' }));
    mocks.mutationError = true;
    rerender(<ModeWizard open currentMode="Shadow" onClose={onClose} />);
    expect(screen.getByText(/Lägesbytet kunde inte bekräftas/)).toBeInTheDocument();
    expect(screen.queryByText(/private-response/)).not.toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});
