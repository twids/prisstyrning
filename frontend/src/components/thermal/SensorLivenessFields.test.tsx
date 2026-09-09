import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { axe } from 'vitest-axe';
import { describe, expect, it, vi } from 'vitest';
import { apiClient } from '../../api/client';
import SensorLivenessFields, { livenessConfigError } from './SensorLivenessFields';
import type { SensorFreshnessRequest } from '../../types/api';

describe('SensorLivenessFields', () => {
  it('väljer rapporteringstid uttryckligen utan att tolka ett vanligt sensorvärde som datum', async () => {
    function Form() {
      const [value, setValue] = useState<SensorFreshnessRequest>({ entityId: 'sensor.room', role: 'room', freshnessEntityId: 'sensor.pressure' });
      return <SensorLivenessFields label="Sovrum" catalog={{ entities: [], nowUtc: Date.now() }} value={value} onChange={changes => setValue({ ...value, ...changes })} />;
    }
    const preview = vi.spyOn(apiClient, 'previewSensorFreshness').mockResolvedValue({ quality: 'Valid', reason: 'Aktuellt', checkedAtUtc: new Date().toISOString(), valueUpdatedUtc: null, livenessUtc: null });
    const view = render(<QueryClientProvider client={new QueryClient()}><Form /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.click(screen.getByRole('checkbox', { name: /Använd entitetens senaste HA-rapport/ }));
    expect(screen.queryByLabelText('Livstecknets attribut för Sovrum')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Testa rapportålder för Sovrum' }));
    expect(preview).toHaveBeenCalledWith(expect.objectContaining({ freshnessEntityId: 'sensor.pressure', freshnessAttribute: '__ha_report_time' }));
    expect((await axe(view.container)).violations).toEqual([]);
    preview.mockRestore();
  });
  it('förhandsgranskar utan att spara och döljer resultat efter policybyte', async () => {
    const now = Date.now();
    const preview = vi.spyOn(apiClient, 'previewSensorFreshness').mockResolvedValue({ quality: 'Valid', reason: 'Aktuellt separat livstecken.', checkedAtUtc: new Date(now).toISOString(), valueUpdatedUtc: new Date(now - 7200000).toISOString(), livenessUtc: new Date(now).toISOString() });
    function Form() {
      const [value, setValue] = useState<SensorFreshnessRequest>({ entityId: 'sensor.room', role: 'room', maximumReportAgeMinutes: 60 });
      return <SensorLivenessFields label="Sovrum" catalog={{ entities: [], nowUtc: now }} value={value} onChange={changes => setValue({ ...value, ...changes })} />;
    }
    const view = render(<QueryClientProvider client={new QueryClient()}><Form /></QueryClientProvider>);
    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Livstecknets attribut för Sovrum'), 'last_seen');
    await user.click(screen.getByRole('button', { name: 'Testa rapportålder för Sovrum' }));
    expect(await screen.findByText('Aktuellt separat livstecken.')).toBeVisible();
    expect(preview).toHaveBeenCalledWith(expect.objectContaining({ entityId: 'sensor.room', freshnessAttribute: 'last_seen', maximumReportAgeMinutes: 60 }));
    expect((await axe(view.container)).violations).toEqual([]);
    await user.clear(screen.getByLabelText('Livstecknets attribut för Sovrum'));
    expect(screen.queryByText('Aktuellt separat livstecken.')).not.toBeInTheDocument();
    expect(preview).toHaveBeenCalledTimes(1);
    preview.mockRestore();
  });

  it('avvisar attributsökvägar och felaktiga entity-ID:n', () => {
    expect(livenessConfigError({ freshnessEntityId: 'https://other.test' })).toBeTruthy();
    expect(livenessConfigError({ freshnessAttribute: 'attributes.last_seen' })).toBeTruthy();
    expect(livenessConfigError({ freshnessAttribute: 'last_seen', freshnessEntityId: 'sensor.room_last_seen' })).toBeNull();
  });

  it('kan inte visa ett gammalt resultat som en ny kontroll efter ommontering', async () => {
    const now = Date.now();
    const preview = vi.spyOn(apiClient, 'previewSensorFreshness').mockResolvedValue({ quality: 'Valid', reason: 'Ett tidigare resultat', checkedAtUtc: new Date(now - 180000).toISOString(), valueUpdatedUtc: null, livenessUtc: null });
    const client = new QueryClient();
    const props = { label: 'Rum', catalog: { entities: [], nowUtc: now }, value: { entityId: 'sensor.room', role: 'room' }, onChange: vi.fn() };
    const view = render(<QueryClientProvider client={client}><SensorLivenessFields key="one" {...props} /></QueryClientProvider>);
    await userEvent.setup().click(screen.getByRole('button', { name: 'Testa rapportålder för Rum' }));
    expect(await screen.findByText(/Kontrollen är äldre än två minuter/)).toBeVisible();
    expect(screen.queryByText('Ett tidigare resultat')).not.toBeInTheDocument();
    view.rerender(<QueryClientProvider client={client}><SensorLivenessFields key="two" {...props} /></QueryClientProvider>);
    expect(screen.queryByText(/Kontrollen är äldre än två minuter/)).not.toBeInTheDocument();
    preview.mockRestore();
  });
});
