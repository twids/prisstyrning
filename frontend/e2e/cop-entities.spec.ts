import { expect, test } from '@playwright/test';

test('realtids-COP och medel-COP hålls åtskilda även på mobil utan styrskrivningar', async ({ page }, testInfo) => {
  const writes: string[] = [];
  page.on('request', request => { if (request.url().includes('/api/') && request.method() !== 'GET') writes.push(request.url()); });
  const now = Date.now();
  const iso = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
  await page.route('**/api/thermal/config', async route => {
    const response = await route.fetch();
    const config = await response.json();
    await route.fulfill({ json: { ...config, site: { ...config.site, updatedAtUtc: iso(-60) }, entities: [
      ...config.entities,
      { role: 'cop_realtime', entityId: 'sensor.cop', expectedUnit: 'COP', enabled: true },
      { role: 'cop_average', entityId: 'sensor.average', expectedUnit: 'COP', enabled: true, averagingPeriod: 'Lifetime' },
    ] } });
  });
  await page.route('**/api/thermal/history*', route => route.fulfill({ json: [{ timestampUtc: iso(0),
    roomTemperaturesJson: '{}', leavingWaterTemperatureC: null, returnWaterTemperatureC: null, cop: null,
    qualityJson: JSON.stringify({ copSource: 'HomeAssistantRealtime', copEntityId: 'sensor.cop', copAverageEntityId: 'sensor.average',
      entities: { cop_realtime: { Quality: 1, Excluded: false, Value: 4.2, ValueUpdatedUtc: iso(-120), Usage: 'HeldWhileIdle' },
        cop_average: { Quality: 1, Excluded: false, Value: 3.8, ValueUpdatedUtc: iso(-120), Usage: 'HistoricalAverage' },
        leaving_water_temperature: { Quality: 1, Excluded: false, Value: 23, ValueUpdatedUtc: iso(-120), Usage: 'HeldWhileIdle' } } }),
  }] }));
  await page.goto('/model');
  await expect(page.getByText('Realtids-COP från Home Assistant', { exact: true })).toBeVisible();
  await expect(page.getByText(/Historiskt värde/)).toBeVisible();
  await expect(page.getByText('Medel-COP från Home Assistant', { exact: true })).toBeVisible();
  await expect(page.getByText(/Livstid · endast uppföljning/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('cop-model.png'), fullPage: true });
  await page.goto('/plan');
  await expect(page.getByText(/Streckade vilovärden/)).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('held-temperatures.png'), fullPage: true });
  expect(writes).toEqual([]);
});
