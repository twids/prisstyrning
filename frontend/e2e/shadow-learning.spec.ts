import { expect, test } from '@playwright/test';

test('bergvärme väljs uttryckligen utan hjälpsensor eller automatisk sparning', async ({ page }) => {
  const mutations: string[] = [];
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/') && r.method() !== 'GET') mutations.push(r.url()); });
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities', exact: true }).click();
  await page.getByRole('switch', { name: 'Anläggningen har ingen avfrostning (till exempel bergvärme)' }).check();
  await expect(page.getByRole('combobox', { name: 'Välj avfrostning aktiv', exact: true })).toHaveCount(0);
  await expect(page.getByText(/Ej tillämpligt — en uttrycklig anläggningsuppgift/)).toBeVisible();
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  expect(mutations).toEqual([]);
});

test('Shadow visar prognos utfärdad före utfall utan påstådd LWT-styrning', async ({ page }) => {
  const now = new Date().toISOString();
  await page.route('**/api/thermal/learning', r => r.fulfill({ json: [{ id: 3, issuedAtUtc: now, twoHourErrorC: null, dayErrorC: null,
    learning: { stage: 'Persistence', samples: 10, heatingSamples: 0, minimumOutsideC: 15, maximumOutsideC: 19,
      trendCPerHour: 0, heldOutMaeC: null, persistenceMaeC: null,
      forecast: [{ timestampUtc: now, predictedC: 21, actualC: null }] } }] }));
  await page.goto('/model');
  await expect(page.getByText('Preliminär baslinje: oförändrad temperatur')).toBeVisible();
  await expect(page.getByRole('img', { name: /Temperaturprognos och uppmätt utfall/ })).toBeVisible();
  await expect(page.getByText(/Värmerespons och LWT-förslag är ännu inte verifierade/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('oförändrat rum visas som antaget och Shadow skiljer avläsningar från nya observationer', async ({ page }, testInfo) => {
  const now = new Date().toISOString();
  const old = new Date(Date.now() - 12 * 3_600_000).toISOString();
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.url());
  });
  await page.route('**/api/thermal/status', route => route.fulfill({ json: {
    mode: 1, dhwWriter: 0, lastTelemetryUtc: now, overallDataQuality: 1,
    dataQualityReason: 'Rapportåldern är osäker. Se rummens antagna värden och saknade källor.',
    emhassAvailable: true, planCreatedUtc: null, planAgeMinutes: null, currentLwtDeviationC: 0,
    fallbackReason: null, nextControlEventUtc: null, manualOverride: false,
  } }));
  await page.route('**/api/thermal/history?*', route => route.fulfill({ json: [{
    timestampUtc: now, roomTemperaturesJson: '{}',
    qualityJson: JSON.stringify({ collectedAtUtc: now, rooms: {
      'sensor.vardagsrum_temperature': { Quality: 1, Excluded: false, Usage: 'AssumedUnchanged', Value: 21.2,
        ValueUpdatedUtc: old, ValueChangedUtc: old, SourceTimestampUtc: old, ReceivedAtUtc: now },
    } }),
  }] }));
  await page.route('**/api/thermal/learning', route => route.fulfill({ json: [{ id: 4, issuedAtUtc: now, twoHourErrorC: null, dayErrorC: null,
    learning: { stage: 'Persistence', samples: 300, assumedSamples: 300, independentSamples: 0, initialTemperatureAssumed: true,
      heatingSamples: 0, minimumOutsideC: 15, maximumOutsideC: 19, trendCPerHour: 0, heldOutMaeC: null, persistenceMaeC: null,
      forecast: Array.from({ length: 96 }, (_, i) => ({ timestampUtc: new Date(Date.parse(now) + (i + 1) * 15 * 60_000).toISOString(),
        predictedC: 21.2, actualC: null })) } }] }));
  await page.goto('/rooms');
  const room = page.getByRole('article', { name: 'Vardagsrum' });
  await expect(room.getByText('Antaget oförändrat', { exact: true })).toBeVisible();
  await expect(room.getByText('21,2 °C', { exact: true })).toBeVisible();
  await expect(room.getByText('Antagen komfortmarginal')).toBeVisible();
  await expect(room.locator('time').first()).toHaveAttribute('datetime', old);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('assumed-room.png'), fullPage: true });
  await page.goto('/model');
  await expect(page.getByText(/Starttemperaturen är antagen oförändrad/)).toBeVisible();
  await expect(page.getByText(/300 punkter med antagen temperatur · 0 oberoende rapporterade observationer/)).toBeVisible();
  await expect(page.getByRole('img', { name: /Temperaturprognos/ }).locator('circle')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('assumed-shadow.png'), fullPage: true });
  expect(mutations).toEqual([]);
});
