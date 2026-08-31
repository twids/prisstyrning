import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';

const root = join(import.meta.dirname, '..', 'wwwroot');
const now = Date.now();
const iso = (minutes = 0) => new Date(now + minutes * 60_000).toISOString();
const roomEntities = ['sensor.vardagsrum_temperature', 'sensor.sovrum_temperature', 'sensor.kontor_temperature'];
const planSteps = Array.from({ length: 192 }, (_, index) => {
  const start = new Date(Math.floor(now / 900_000) * 900_000 + index * 900_000);
  const hour = start.getHours() + start.getMinutes() / 60;
  const price = .45 + Math.max(0, Math.sin((hour - 6) / 24 * Math.PI * 2)) * 1.4;
  const dhw = index >= 22 && index < 26;
  const predicted = 21.25 + Math.sin(index / 18) * .24;
  return {
    id: index + 1, thermalPlanId: 'fixture-plan', startUtc: start.toISOString(), endUtc: new Date(start.getTime() + 900_000).toISOString(),
    desiredHeatOutputKw: dhw ? 0 : 3.2 + Math.sin(index / 7), desiredLwtDeviationC: dhw ? 0 : Math.round(Math.sin(index / 11) * 2) / 2,
    dhwReserved: dhw, dhwMode: dhw ? 'Eco' : '', incrementalCost: price * .35, confidence: .84,
    expectedRoomsJson: JSON.stringify({ representative: predicted }),
    decisionReasonJson: JSON.stringify({ mainReason: dhw ? 'Kompressorkapaciteten är reserverad för varmvatten.' : 'EMHASS minimerar kostnaden inom komfortbandet.', price, comfortMarginC: predicted - 20.8, modelConfidence: .84, alternative: null }),
  };
});
const history = Array.from({ length: 288 }, (_, index) => {
  const timestamp = new Date(now - (287 - index) * 300_000);
  const room = 21.25 + Math.sin(index / 20) * .18;
  return {
    id: index + 1, userId: 'default', timestampUtc: timestamp.toISOString(), outsideTemperatureC: 2.4 + Math.sin(index / 30), outsideTemperatureForecastJson: '[]', windSpeedMps: 3.1, solarIrradianceWm2: 0,
    leavingWaterTemperatureC: 34.2, returnWaterTemperatureC: 29.8, flowLitresPerMinute: 13.4, brineInC: 3.1, brineOutC: .7,
    tankTemperatureC: 47.8, heatPumpPowerKw: 1.42, propertyPowerKw: 2.1, spotPriceSekPerKwh: .72, heatOutputKw: 4.1, cop: 3.34,
    dhwActive: false, defrostActive: false, backupHeaterActive: false,
    roomTemperaturesJson: JSON.stringify({ [roomEntities[0]]: room, [roomEntities[1]]: room - .25, [roomEntities[2]]: room + .12 }),
    qualityJson: JSON.stringify({ rooms: Object.fromEntries(roomEntities.map(entityId => [entityId, { Quality: 0, Excluded: false, Reason: null }])) }),
  };
});
const site = { userId: 'default', controlMode: 'Shadow', dhwWriter: 'Legacy', baseRoomTargetC: 21.5, lowerComfortBandC: .5, upperComfortBandC: .7, activeDeviationLimitC: 1, tariffEnabled: false, heatPumpPowerSignVerified: true, weatherCurveVerified: false, comfortSetpointConfirmed: true, comfortSetpointC: 60, comfortIntervalDays: 21, comfortFlexibilityDays: 7, timeZone: 'Europe/Stockholm', variableCostComponentsJson: '{"energiskatt":0.55,"rörligt_nät":0.12}', tariffDefinitionJson: '{}', createdAtUtc: iso(-40_000), updatedAtUtc: iso(-30) };
const rooms = [
  { id: 1, userId: 'default', name: 'Vardagsrum', entityId: roomEntities[0], targetOffsetC: 0, weight: 2, isCritical: true, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3 },
  { id: 2, userId: 'default', name: 'Sovrum', entityId: roomEntities[1], targetOffsetC: -.3, weight: 1, isCritical: false, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3 },
  { id: 3, userId: 'default', name: 'Kontor', entityId: roomEntities[2], targetOffsetC: .1, weight: 1, isCritical: true, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3 },
];
const haEntities = [...roomEntities.map((entityId, index) => ({ entityId, friendlyName: rooms[index].name, state: String(21.2 + index * .1), unit: '°C', lastUpdatedUtc: iso(-2), receivedAtUtc: iso(0), quality: 0, qualityReason: null })),
  { entityId: 'sensor.altherma_lwt', friendlyName: 'Altherma framledning', state: '34.2', unit: '°C', lastUpdatedUtc: iso(-1), receivedAtUtc: iso(0), quality: 0, qualityReason: null },
  { entityId: 'number.altherma_deviation_heating', friendlyName: 'Deviation Heating', state: '0', unit: '°C', lastUpdatedUtc: iso(-1), receivedAtUtc: iso(0), quality: 0, qualityReason: null }];
const events = [
  { id: 3, userId: 'default', timestampUtc: iso(-8), severity: 'Information', category: 'Optimizer', message: 'Start 12:20 eftersom hela cykeln beräknas kosta 1,84 kr.', detailsJson: '{}' },
  { id: 2, userId: 'default', timestampUtc: iso(-55), severity: 'Warning', category: 'ModelDrift', message: 'Grundkurvetestet behöver ytterligare tre uppvärmningsdygn.', detailsJson: '{}' },
  { id: 1, userId: 'default', timestampUtc: iso(-120), severity: 'Information', category: 'DataQuality', message: 'Alla kritiska rumsgivare är giltiga igen.', detailsJson: '{}' },
];
const checks = [
  ['ha-telemetry-configured', 'Home Assistant-telemetri är separat konfigurerad', true, 'Ingen åtgärd krävs.'],
  ['ha-snapshot', 'En färsk startbild finns från Home Assistant', true, 'Ingen åtgärd krävs.'],
  ['telemetry-fresh', 'Senaste femminuterstelemetri är högst tio minuter gammal', true, 'Ingen åtgärd krävs.'],
  ['critical-room', 'Minst ett kritiskt rum har en aktiverad givare', true, 'Ingen åtgärd krävs.'],
  ['weather-curve', 'Grundkurvan är verifierad med avvikelse noll', false, 'Genomför minst sju verkliga uppvärmningsdygn och bekräfta grundkurvan.'],
].map(([key, requirement, passed, action]) => ({ key, requirement, passed, action, severity: passed ? 'Information' : 'ActionRequired' }));

function fixture(pathname) {
  if (pathname === '/api/session') return { authenticated: true, userId: 'default', isAdmin: true, csrfToken: 'visual-qa-csrf-token' };
  if (pathname === '/api/admin/status') return { isAdmin: true, userId: 'default' };
  // Use ASP.NET's actual numeric wire enums, not the UI's translated names.
  if (pathname === '/api/thermal/status') return { mode: 1, dhwWriter: 0, lastTelemetryUtc: iso(-2), overallDataQuality: 0, dataQualityReason: 'Alla 3 aktiverade datakällor är giltiga i senaste insamlingen.', emhassAvailable: true, planCreatedUtc: iso(-7), planAgeMinutes: 7, currentLwtDeviationC: 0, fallbackReason: null, nextControlEventUtc: iso(42), manualOverride: false };
  if (pathname === '/api/thermal/config') return { site, rooms, entities: [] };
  if (pathname === '/api/thermal/readiness') return { targetMode: 2, ready: false, checks };
  if (pathname === '/api/thermal/plan') return { id: 'fixture-plan', userId: 'default', createdAtUtc: iso(-7), validFromUtc: planSteps[0].startUtc, validUntilUtc: planSteps[planSteps.length - 1].endUtc, status: 'Valid', isShadow: true, solverDurationMs: 1830, objectiveCost: 32.47, confidence: .84, summary: 'Start 12:20 eftersom hela cykeln beräknas kosta 1,84 kr.', inputSnapshotJson: '{}', steps: planSteps };
  if (pathname === '/api/thermal/history') return history;
  if (pathname === '/api/thermal/events') return events;
  if (pathname === '/api/thermal/dhw') return [{ id: 1, kind: 'Eco', source: 'Shadow', status: 'Planned', plannedStartUtc: iso(42), scheduleAcceptedUtc: null, actualStartUtc: null, targetReachedUtc: null, actualEndUtc: null, startTemperatureC: 47.8, targetTemperatureC: 50, predictedDurationMinutes: 45, reservedDurationMinutes: 55, predictedCost: 1.84, actualCost: null, backupHeaterUsed: false, targetVerificationCount: 0, estimatedCompletionUtc: iso(97) }];
  if (pathname === '/api/thermal/models') return [{ id: 4, modelType: '2R2C', createdAtUtc: iso(-600), trainingFromUtc: iso(-60 * 24 * 30), trainingToUtc: iso(-600), isActive: true, parametersJson: JSON.stringify({ envelopeConductanceKwPerC: .34, massCapacityKwhPerC: 38.2, massCouplingKwPerC: .81, baseCurveSlope: -.46 }), metricsJson: JSON.stringify({ twoHourMaeC: .21, dayMaeC: .47 }) }];
  if (pathname === '/api/home-assistant/status') return { configured: true, connected: true, phase: 'Connected', configurationUpdatedAtUtc: iso(-60), lastSnapshotUtc: iso(-2), lastActivityUtc: iso(-1), cachedEntities: haEntities.length };
  if (pathname === '/api/home-assistant/config') return { baseUrl: 'https://ha.example.se', telemetryEnabled: true, controlEnabled: false, heatingDeviationEntityId: 'number.altherma_deviation_heating', staleAfterMinutes: 10, telemetryTokenConfigured: true, controlTokenConfigured: false, updatedAtUtc: iso(-60) };
  if (pathname === '/api/home-assistant/entities') return haEntities.map(entity => ({
    ...entity, compatibleUnits: ['°C'], checkedAtUtc: new Date().toISOString(),
    validUntilUtc: new Date(Date.parse(entity.lastUpdatedUtc) + 10 * 60_000).toISOString(),
  }));
  if (pathname === '/api/home-assistant/import-history') return { importedSamples: 8460, existingSamplesPreserved: 180, requestedEntities: 12, entitiesWithoutHistory: [] };
  return null;
}

createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', 'http://127.0.0.1');
  if (url.pathname.startsWith('/api/')) {
    const data = fixture(url.pathname);
    response.writeHead(data == null ? 404 : 200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify(data ?? { error: 'Fixture saknas' }));
    return;
  }
  const requested = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
  let file = normalize(join(root, requested));
  try { if ((await stat(file)).isDirectory()) file = join(file, 'index.html'); }
  catch { file = join(root, 'index.html'); }
  try {
    const body = await readFile(file);
    const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.map': 'application/json', '.svg': 'image/svg+xml' }[extname(file)] ?? 'application/octet-stream';
    response.writeHead(200, { 'Content-Type': `${mime}; charset=utf-8` }); response.end(body);
  } catch { response.writeHead(404); response.end('Not found'); }
}).listen(4174, '127.0.0.1', () => console.log('Visual QA: http://127.0.0.1:4174/'));
