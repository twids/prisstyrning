import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { axe } from 'vitest-axe';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EntitiesTab } from './ThermalSettingsPage';
import type { ThermalConfig } from '../../types/api';

function Harness() {
  const [queryClient] = useState(() => new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } }));
  const [draft, setDraft] = useState({ site: {}, rooms: [], entities: [
    { id: 1, role: 'cop_realtime', entityId: 'sensor.cop', expectedUnit: 'COP', enabled: true },
    { id: 2, role: 'cop_average', entityId: 'sensor.average', expectedUnit: 'COP', enabled: true },
  ] } as unknown as ThermalConfig);
  return <QueryClientProvider client={queryClient}><EntitiesTab draft={draft} setDraft={setDraft} catalog={{ nowUtc: Date.now(), entities: [] }} />
    <output aria-label="Vald COP-medelperiod">{draft.entities.find(x => x.role === 'cop_average')?.averagingPeriod ?? 'Unknown'}</output></QueryClientProvider>;
}
describe('COP-inställningar', () => {
  it('separerar realtid och medel och kan spara val av period i utkastet', async () => {
    render(<Harness />);
    expect(screen.getByText('Realtids-COP (valfri)')).toBeInTheDocument();
    expect(screen.getByText('Medel-COP (valfri)')).toBeInTheDocument();
    await userEvent.setup().selectOptions(screen.getByLabelText('Medelperiod för COP'), 'Lifetime');
    expect(screen.getByLabelText('Vald COP-medelperiod')).toHaveTextContent('Lifetime');
    expect(screen.getByText(/dold reserv/)).toBeInTheDocument();
  });
  it('har tillgängliga val och lägger rapportinställningar under Avancerat', async () => {
    const { container } = render(<Harness />);
    expect(screen.getByRole('button', { name: 'Avancerat: rapportering för realtids-cop (valfri)' })).toHaveAttribute('aria-expanded', 'false');
    expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toEqual([]);
  });
});
