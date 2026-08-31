import { describe, expect, it } from 'vitest';
import type { HomeAssistantEntity } from '../../types/api';
import { assessEntityChoice } from './entityCatalog';

const now = Date.parse('2026-08-31T04:00:00Z');
const iso = (offsetSeconds: number) => new Date(now + offsetSeconds * 1000).toISOString();
const entity: HomeAssistantEntity = {
  entityId: 'sensor.room', friendlyName: 'Vardagsrum', state: '21.5', unit: '°C',
  lastUpdatedUtc: iso(-60), receivedAtUtc: iso(-30), checkedAtUtc: iso(0), validUntilUtc: iso(540),
  quality: 'Valid', qualityReason: null, compatibleUnits: ['°C'],
};

describe('preliminär entity-kontroll', () => {
  it('kräver både färska värden och serverns enhetskontroll', () => {
    expect(assessEntityChoice(entity, '°C', now).quality).toBe('Valid');
    expect(assessEntityChoice({ ...entity, state: '68', unit: '°F' }, '°C', now).quality).toBe('Valid');
    expect(assessEntityChoice({ ...entity, state: '1500', unit: 'W', compatibleUnits: ['kW'] }, 'kW', now).quality).toBe('Valid');
  });

  it('gissar inte att energi är effekt eller att ett tal är en boolesk signal', () => {
    expect(assessEntityChoice({ ...entity, unit: 'kWh', compatibleUnits: ['kWh'] }, 'kW', now))
      .toEqual(expect.objectContaining({ quality: 'Invalid', reason: expect.stringContaining('kW') }));
    expect(assessEntityChoice({ ...entity, state: '0' }, 'bool', now).quality).toBe('Invalid');
    expect(assessEntityChoice({ ...entity, state: 'off', unit: null, compatibleUnits: ['bool'] }, 'bool', now).quality).toBe('Valid');
  });

  it.each(['unknown', ' UNAVAILABLE ', ''])('godkänner aldrig otillgängligt råvärde %s', (state) => {
    expect(assessEntityChoice({ ...entity, state }, '°C', now).quality).toBe('Unavailable');
  });

  it.each(['Stale', 'Invalid', 'Unavailable'] as const)('bevarar serverns %s även med enhetsmatchning', (quality) => {
    expect(assessEntityChoice({ ...entity, quality, qualityReason: 'Verifierad orsak' }, '°C', now))
      .toEqual({ quality, reason: 'Verifierad orsak' });
  });

  it.each(['lastUpdatedUtc', 'receivedAtUtc', 'checkedAtUtc', 'validUntilUtc'] as const)('kräver en verifierbar %s', (field) => {
    expect(assessEntityChoice({ ...entity, [field]: 'invalid-date' }, '°C', now).quality).toBe('Unavailable');
  });

  it.each(['lastUpdatedUtc', 'receivedAtUtc', 'checkedAtUtc'] as const)('avvisar framtida %s', (field) => {
    expect(assessEntityChoice({ ...entity, [field]: iso(90) }, '°C', now).quality).toBe('Invalid');
  });

  it('avvisar mottagning före uppdatering och passerad giltighetstid', () => {
    expect(assessEntityChoice({ ...entity, receivedAtUtc: iso(-200) }, '°C', now).quality).toBe('Invalid');
    expect(assessEntityChoice({ ...entity, validUntilUtc: iso(-1) }, '°C', now).quality).toBe('Stale');
  });

  it('åldrar den lokala katalogbedömningen även om sensorgränsen är längre', () => {
    expect(assessEntityChoice(entity, '°C', now + 121_000))
      .toEqual(expect.objectContaining({ quality: 'Stale', reason: expect.stringContaining('två minuter') }));
  });

  it('äldre API utan de additiva kontrollfälten innebär okänd kontroll, inte grönt', () => {
    const { compatibleUnits: _, checkedAtUtc: __, validUntilUtc: ___, ...older } = entity;
    expect(assessEntityChoice(older, '°C', now).quality).toBe('Unavailable');
    expect(assessEntityChoice({ ...entity, compatibleUnits: undefined }, '°C', now).quality).toBe('Unavailable');
  });

  it('skiljer en väderetikett från en läsbar framtida prognos', () => {
    const weather = { ...entity, state: 'sunny', unit: null, compatibleUnits: [] };
    expect(assessEntityChoice(weather, 'forecast', now).reason).toContain('forecast-attributet');
    expect(assessEntityChoice({ ...weather, compatibleUnits: ['forecast'] }, 'forecast', now).quality).toBe('Valid');
  });

  it('prioriterar uppdateringsfel och saknad entity framför cachad godkänd kontroll', () => {
    expect(assessEntityChoice(entity, '°C', now, 'Sensorlistan kunde inte uppdateras.'))
      .toEqual({ quality: 'Unavailable', reason: 'Sensorlistan kunde inte uppdateras.' });
    expect(assessEntityChoice(null, '°C', now).reason).toContain('Mappningen är kvar');
  });
});
