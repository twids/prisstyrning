import { describe, expect, it } from 'vitest';
import type { ThermalStatus } from '../../types/api';
import { describeEmhass } from './emhassStatus';

const now = Date.parse('2026-09-06T00:00:00Z');
const status: ThermalStatus = {
  mode: 'Legacy', dhwWriter: 'Legacy', lastTelemetryUtc: null, overallDataQuality: 'Unavailable',
  emhassAvailable: false, emhassEnabled: true, planCreatedUtc: null, planAgeMinutes: null,
  currentLwtDeviationC: 0, fallbackReason: null, nextControlEventUtc: null, manualOverride: false,
  emhassConnection: { reachable: true, checkedUtc: new Date(now).toISOString() },
};

describe('EMHASS connection and planning are separate', () => {
  it('explains a reachable service with no optimizer execution in Legacy', () => {
    const result = describeEmhass(status, now);
    expect(result.label).toBe('EMHASS ansluten');
    expect(result.detail).toContain('Det verifierar inte solvern');
    expect(result.detail).toContain('Ingen optimering körs i Legacy');
  });
  it('does not hide connection failure behind earlier solver success', () => {
    expect(describeEmhass({ ...status, emhassAvailable: true,
      emhassConnection: { reachable: false, checkedUtc: new Date(now).toISOString() } }, now).label)
      .toBe('EMHASS anslutningsfel');
  });
  it.each([null, 'invalid', new Date(now - 120_001).toISOString(), new Date(now + 1).toISOString()])(
    'does not present stale or invalid evidence as connected (%s)', (checkedUtc) => {
      expect(describeEmhass({ ...status, emhassConnection: { reachable: true, checkedUtc } }, now).connected).toBe(false);
    });
  it('disabled takes precedence over cached connectivity', () => {
    expect(describeEmhass({ ...status, emhassEnabled: false }, now).label).toBe('EMHASS avstängd');
  });
  it('a connection does not imply a valid plan in Shadow', () => {
    expect(describeEmhass({ ...status, mode: 'Shadow' }, now).detail).toContain('Ingen plan finns ännu');
  });
});
