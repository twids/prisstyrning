import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ModeWizard from './ModeWizard';

const mocks = vi.hoisted(() => ({
  reset: vi.fn(),
  mutateAsync: vi.fn(),
  readiness: {
    data: {
      targetMode: 'LwtActive' as const,
      ready: false,
      checks: [
        { key: 'telemetry', requirement: 'Färsk telemetri', passed: true, action: 'Ingen åtgärd krävs.', severity: 'Information' as const },
        { key: 'weather', requirement: 'Verifierad grundkurva', passed: false, action: 'Slutför grundkurvetestet.', severity: 'ActionRequired' as const },
      ],
    },
    isLoading: false,
    isError: false,
    error: null,
  },
}));

vi.mock('../../hooks/thermal/useThermal', () => ({
  useThermalReadiness: () => mocks.readiness,
  useChangeThermalMode: () => ({
    mutateAsync: mocks.mutateAsync,
    reset: mocks.reset,
    isPending: false,
    isError: false,
    error: null,
  }),
}));

describe('ModeWizard', () => {
  beforeEach(() => vi.clearAllMocks());

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
});
