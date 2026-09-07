import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { describe, it, expect, vi } from 'vitest';
import ShadowLearningPanel from './ShadowLearningPanel';
import { apiClient } from '../../api/client';
import type { ShadowLearningVersion } from '../../types/api';

const version: ShadowLearningVersion = { id: 1, issuedAtUtc: '2026-09-01T10:00:00Z', twoHourErrorC: .2, dayErrorC: null,
  learning: { stage: 'Persistence', samples: 20, heatingSamples: 0, minimumOutsideC: 15, maximumOutsideC: 20,
    trendCPerHour: 0, heldOutMaeC: null, persistenceMaeC: null,
    forecast: [{ timestampUtc: '2026-09-01T12:00:00Z', predictedC: 21, actualC: 21.2 }] } };

describe('Shadow learning', () => {
  it('labels initial uncertainty, actual outcome and missing LWT capability accessibly', async () => {
    const refresh = vi.fn();
    const { container } = render(<main><ShadowLearningPanel versions={[version]} failed={false} refresh={refresh} /></main>);
    expect(screen.getByText(/Preliminär baslinje/)).toBeInTheDocument();
    expect(screen.getByText(/Värmerespons och LWT-förslag är ännu inte verifierade/)).toBeInTheDocument();
    expect(screen.getByText(/Ett icke-kritiskt rum med vikt 0 används bara för uppföljning/)).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /Temperaturprognos/ })).toBeInTheDocument();
    expect(refresh).not.toHaveBeenCalled();
    expect((await axe(container)).violations).toEqual([]);
  });

  it('only asks to update the write-free model after explicit click', async () => {
    const update = vi.spyOn(apiClient, 'updateShadowLearning').mockResolvedValue([]);
    const refresh = vi.fn().mockResolvedValue({});
    render(<ShadowLearningPanel versions={[]} failed={false} refresh={refresh} />);
    expect(update).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Uppdatera skrivfri Shadow-modell' }));
    expect(update).toHaveBeenCalledTimes(1);
    expect(refresh).toHaveBeenCalledTimes(1);
    update.mockRestore();
  });

  it('does not present cached predictions as verified after a failed fetch', () => {
    render(<ShadowLearningPanel versions={[version]} failed refresh={vi.fn()} />);
    expect(screen.getByText(/kunde inte hämtas eller uppdateras/)).toBeInTheDocument();
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('separates assumed snapshots from independent observations and verified outcomes', async () => {
    const assumed = { ...version, twoHourErrorC: null, learning: { ...version.learning,
      samples: 300, assumedSamples: 300, independentSamples: 0, initialTemperatureAssumed: true,
      forecast: [{ timestampUtc: '2026-09-01T12:00:00Z', predictedC: 21, actualC: null }] } };
    const { container } = render(<main><ShadowLearningPanel versions={[assumed]} failed={false} refresh={vi.fn()} /></main>);
    expect(screen.getByText(/300 punkter med antagen temperatur · 0 oberoende rapporterade observationer/)).toBeInTheDocument();
    expect(screen.getByText(/Starttemperaturen är antagen oförändrad/)).toBeInTheDocument();
    expect(screen.getByText(/Rapportålder ensam blockerar inte startprognosen/)).toBeInTheDocument();
    expect(screen.getByText(/Väntar på utfall/)).toBeInTheDocument();
    expect(container.querySelectorAll('svg[role="img"] circle')).toHaveLength(0);
    expect((await axe(container)).violations).toEqual([]);
  });
});
