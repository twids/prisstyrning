import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ThermalEvent } from '../../types/api';
import ThermalEventsPage from './ThermalEventsPage';

const hooks = vi.hoisted(() => ({ events: vi.fn() }));
vi.mock('../../hooks/thermal/useThermal', () => ({ useThermalEvents: hooks.events }));
const refresh = vi.fn().mockResolvedValue({});
const evidenceReason = 'COP-modellen: underlaget behöver tränas om. Senaste giltiga plan används högst 60 minuter.';
const records: ThermalEvent[] = [
  { id: 1, userId: 'test', timestampUtc: '2026-08-31T18:00:00Z', severity: 'Warning', category: 'Optimizer', message: evidenceReason, detailsJson: '{"internal":"not-for-display"}' },
  { id: 2, userId: 'test', timestampUtc: '2026-08-31T17:00:00Z', severity: 'Information', category: 'DataQuality', message: 'Givaren används igen.', detailsJson: '{}' },
];
const query = (data: ThermalEvent[] | undefined = records) => ({ data, isLoading: false, isError: false, isFetching: false, refetch: refresh });
const view = () => render(<main><ThermalEventsPage /></main>);

describe('ThermalEventsPage', () => {
  beforeEach(() => { refresh.mockClear(); hooks.events.mockReturnValue(query()); });

  it('explains rejected model evidence as dated history, not an active alarm', () => {
    const { container } = view();
    expect(screen.getByText(evidenceReason)).toBeInTheDocument();
    expect(screen.getByText('Optimering')).toBeInTheDocument();
    expect(screen.getByText(/inte en lista över aktiva larm/)).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Revisionslogg' })).toBeInTheDocument();
    expect(container.querySelector('time')).toHaveAttribute('datetime', '2026-08-31T18:00:00Z');
    expect(screen.getByText(/20:00 · svensk tid/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText(/not-for-display/)).not.toBeInTheDocument();
    expect(within(screen.getAllByRole('listitem')[0]).getByText('Varning')).toBeVisible();
  });

  it('filters with the keyboard and refreshes only the history query', async () => {
    const user = userEvent.setup();
    view();
    screen.getByRole('combobox', { name: 'Nivå' }).focus();
    await user.keyboard('{ArrowDown}');
    await user.click(screen.getByRole('option', { name: 'Varning' }));
    expect(screen.getByText(evidenceReason)).toBeInTheDocument();
    expect(screen.queryByText('Givaren används igen.')).not.toBeInTheDocument();
    screen.getByRole('button', { name: 'Hämta historik igen' }).focus();
    await user.keyboard('{Enter}');
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('labels cached history when an update fails without exposing the raw error', () => {
    hooks.events.mockReturnValue({ ...query(), isError: true, error: new Error('private-server-response') });
    view();
    expect(screen.getByRole('alert')).toHaveTextContent('Visar tidigare hämtade händelser');
    expect(screen.getByText(evidenceReason)).toBeInTheDocument();
    expect(screen.queryByText(/private-server-response/)).not.toBeInTheDocument();
    expect(screen.queryByText('Inga händelser matchar filtret.')).not.toBeInTheDocument();
  });

  it('does not claim an empty log or zero warnings on initial fetch failure', () => {
    hooks.events.mockReturnValue({ ...query([]), isError: true });
    view();
    expect(screen.getByRole('alert')).toHaveTextContent('Händelserna kunde inte hämtas');
    expect(screen.queryByText('Inga händelser matchar filtret.')).not.toBeInTheDocument();
    expect(screen.queryByText('Varning 0')).not.toBeInTheDocument();
  });

  it('distinguishes loading from a successfully empty log', () => {
    hooks.events.mockReturnValue({ ...query([]), isLoading: true, isFetching: true });
    const rendered = view();
    expect(screen.getByRole('status')).toHaveTextContent('Hämtar händelser');
    expect(screen.getByRole('button', { name: 'Hämta historik igen' })).toBeDisabled();
    expect(screen.queryByText('Inga händelser matchar filtret.')).not.toBeInTheDocument();
    hooks.events.mockReturnValue(query([]));
    rendered.rerender(<main><ThermalEventsPage /></main>);
    expect(screen.getByText('Inga händelser matchar filtret.')).toBeInTheDocument();
  });

  it('handles invalid timestamps without crashing or inventing a date', () => {
    hooks.events.mockReturnValue(query([{ ...records[0], timestampUtc: 'invalid' }]));
    view();
    expect(screen.getByText('Okänd tid')).toBeInTheDocument();
    expect(screen.getByText(evidenceReason)).toBeInTheDocument();
  });

  it('has named controls, list semantics and no detectable accessibility violations', async () => {
    const { container } = view();
    expect(screen.getByRole('list', { name: 'Sparade händelser' })).toBeInTheDocument();
    expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([]);
  });
});
