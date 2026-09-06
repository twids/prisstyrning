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
});
