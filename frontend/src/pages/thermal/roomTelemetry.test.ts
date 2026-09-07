import { describe, expect, it } from 'vitest';
import type { ThermalRoomConfig } from '../../types/api';
import { describeRoomReading } from './roomTelemetry';

const now = Date.parse('2026-08-31T00:45:00Z');
const room: ThermalRoomConfig = {
  id: 1, userId: 'test', name: 'Rum', entityId: 'sensor.room', targetOffsetC: 0,
  weight: 1, isCritical: true, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3,
};
const sample = {
  timestampUtc: new Date(now - 60_000).toISOString(),
  roomTemperaturesJson: '{"sensor.room":21.2}',
  qualityJson: '{"rooms":{"sensor.room":{"Quality":0,"Excluded":false}}}',
};
const qualityJson = (quality: unknown, excluded: unknown = false) => JSON.stringify({ rooms: { [room.entityId]: { quality, excluded } } });

describe('describeRoomReading', () => {
  it.each([0, 'Valid', 'valid'])('accepterar giltig kvalitet %s utan att ändra mätvärdet', quality => {
    expect(describeRoomReading(room, { ...sample, qualityJson: qualityJson(quality) }, now))
      .toMatchObject({ current: true, value: 21.2, kind: 'measurement', status: 'Valid' });
  });

  it('läser live-snapshotens PascalCase-fält', () => {
    expect(describeRoomReading(room, sample, now).current).toBe(true);
  });

  it('håller en återhämtande givare exkluderad även när senaste mätningen var giltig', () => {
    expect(describeRoomReading(room, { ...sample, qualityJson: qualityJson(0, true) }, now))
      .toMatchObject({ status: 'Excluded', current: false, kind: 'fallback', value: 21.2 });
  });

  it.each([[1, 'Stale'], [2, 'Invalid'], [3, 'Unavailable']] as const)('märker kritiska rummets reservvärde vid kvalitet %s', (quality, status) => {
    expect(describeRoomReading(room, { ...sample, qualityJson: qualityJson(quality) }, now))
      .toMatchObject({ status, current: false, kind: 'fallback', value: 21.2 });
  });

  it('påstår inte att en icke-kritisk givare har ett reservvärde', () => {
    expect(describeRoomReading({ ...room, isCritical: false }, { ...sample, qualityJson: qualityJson(2) }, now))
      .toMatchObject({ status: 'Invalid', current: false, kind: 'none', value: null });
  });

  it('tillåter exakt tio minuter men inte en äldre snapshot', () => {
    const boundary = { ...sample, timestampUtc: new Date(now - 10 * 60_000).toISOString() };
    expect(describeRoomReading(room, boundary, now).current).toBe(true);
    expect(describeRoomReading(room, boundary, now + 1)).toMatchObject({ status: 'Stale', current: false, kind: 'measurement' });
  });

  it.each(['not-a-date', '2026-08-31T01:00:00Z'])('godkänner inte tidsstämpeln %s', timestampUtc => {
    expect(describeRoomReading(room, { ...sample, timestampUtc }, now))
      .toMatchObject({ status: 'Unknown', current: false, value: null });
  });

  it('visar även nyligen importerad historik som historik, inte som aktuell telemetri', () => {
    const imported = { ...sample, qualityJson: JSON.stringify({ source: 'HomeAssistantHistoryImport', rooms: { [room.entityId]: { quality: 0, excluded: false } } }) };
    expect(describeRoomReading(room, imported, now)).toMatchObject({ status: 'Imported', current: false, kind: 'measurement' });
  });

  it.each([
    ['homeassistanthistoryimport', 'Imported'], ['unknown-source', 'Unknown'], [null, 'Unknown'],
  ])('räknar inte en angiven historik- eller okänd källa som en aktuell mätning: %s', (source, status) => {
    const history = { ...sample, qualityJson: JSON.stringify({ source, rooms: { [room.entityId]: { Quality: 0, Excluded: false } } }) };
    expect(describeRoomReading(room, history, now)).toMatchObject({ status, current: false });
  });

  it.each(['{', 'null', '[]', '42', '{}', '{"rooms":{"sensor.room":{"Quality":0}}}', qualityJson('future-quality'), qualityJson(0, 'false')])('behandlar okänd eller felaktig kvalitetsmetadata säkert: %s', json => {
    expect(describeRoomReading(room, { ...sample, qualityJson: json }, now))
      .toMatchObject({ status: 'Unknown', current: false, value: null });
  });

  it.each(['{', 'null', '[]', '{}', '{"sensor.room":"21.2"}', '{"sensor.room":null}', '{"sensor.room":500}', '{"sensor.room":1e309}'])('behandlar felaktigt mätvärde säkert: %s', json => {
    expect(describeRoomReading(room, { ...sample, roomTemperaturesJson: json }, now))
      .toMatchObject({ status: 'Invalid', current: false, value: null });
  });

  it('använder inte cachade mätvärden efter ett hämtningsfel', () => {
    expect(describeRoomReading(room, sample, now, true)).toMatchObject({ status: 'FetchError', current: false, value: null });
  });

  it('räknar inte ett inaktiverat rum som giltigt eller som ett nytt givarfel', () => {
    expect(describeRoomReading({ ...room, enabled: false }, sample, now)).toMatchObject({ status: 'Disabled', current: false, value: null });
  });

  it('hanterar att ingen snapshot finns', () => {
    expect(describeRoomReading(room, undefined, now)).toMatchObject({ status: 'Unavailable', current: false, value: null });
  });

  const assumed = () => ({ ...sample, roomTemperaturesJson: '{"sensor.room":25}', qualityJson: JSON.stringify({
    collectedAtUtc: sample.timestampUtc,
    rooms: { [room.entityId]: { Quality: 1, Excluded: false, Usage: 'AssumedUnchanged', Value: 21.2,
      ValueUpdatedUtc: new Date(now - 12 * 3_600_000).toISOString(), SourceTimestampUtc: new Date(now - 12 * 3_600_000).toISOString(),
      ValueChangedUtc: new Date(now - 12 * 3_600_000).toISOString(), ReceivedAtUtc: sample.timestampUtc } },
  }) });

  it('visar uttryckligen antaget värde från HA, aldrig det syntetiska reservvärdet', () => {
    expect(describeRoomReading(room, assumed(), now)).toMatchObject({
      status: 'AssumedUnchanged', kind: 'assumed', current: false, value: 21.2,
      receivedAtUtc: sample.timestampUtc,
    });
  });

  it('kräver inte kontrollreserv eller kritiskt rum för att visa antagen temperatur', () => {
    expect(describeRoomReading({ ...room, isCritical: false }, { ...assumed(), roomTemperaturesJson: '{}' }, now))
      .toMatchObject({ status: 'AssumedUnchanged', value: 21.2, current: false });
  });

  it.each(['history', 'excluded', 'invalid', 'unavailable', 'old-receipt', 'missing-receipt', 'future-report', 'bad-value', 'old-marker', 'duplicate-quality'])('godkänner inte antaget oförändrat vid %s', fault => {
    const snapshot = assumed();
    const metadata = JSON.parse(snapshot.qualityJson);
    const assessment = metadata.rooms[room.entityId];
    if (fault === 'history') metadata.source = 'HomeAssistantHistoryImport';
    if (fault === 'excluded') assessment.Excluded = true;
    if (fault === 'invalid') assessment.Quality = 2;
    if (fault === 'unavailable') assessment.Quality = 3;
    if (fault === 'old-receipt') assessment.ReceivedAtUtc = new Date(now - 11 * 60_000).toISOString();
    if (fault === 'missing-receipt') delete assessment.ReceivedAtUtc;
    if (fault === 'future-report') assessment.SourceTimestampUtc = new Date(now + 60_000).toISOString();
    if (fault === 'bad-value') assessment.Value = 100;
    if (fault === 'old-marker') assessment.Usage = 'HeldRoom';
    if (fault === 'duplicate-quality') assessment.quality = 0;
    snapshot.qualityJson = JSON.stringify(metadata);
    expect(describeRoomReading(room, snapshot, now).status).not.toBe('AssumedUnchanged');
  });

  it('slutar anta aktuell temperatur när kommunikationen eller hämtningen inte längre kan verifieras', () => {
    expect(describeRoomReading(room, assumed(), now + 11 * 60_000).status).toBe('Stale');
    expect(describeRoomReading(room, assumed(), now, true).status).toBe('FetchError');
  });
});
