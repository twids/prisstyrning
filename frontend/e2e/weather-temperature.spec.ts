import { expect, test } from '@playwright/test';

test('weather-val testar separat timprognos utan att spara konfiguration', async ({ page }) => {
  let writes = 0;
  await page.route('**/api/thermal/config', async route => {
    if (route.request().method() !== 'GET') writes++;
    await route.continue();
  });
  await page.route('**/api/home-assistant/entities', route => route.fulfill({ json: [
    { entityId: 'weather.test', friendlyName: 'Testväder', state: 'sunny', quality: 1, compatibleUnits: [], receivedAtUtc: new Date().toISOString() },
  ] }));
  await page.route('**/api/thermal/weather/test', route => {
    expect(route.request().postDataJSON()).toEqual({ entityId: 'weather.test' });
    return route.fulfill({ json: { quality: 0, reason: null, points: [1, 2].map(hour => ({ timestampUtc: new Date(Date.now() + hour * 3600000).toISOString(), temperatureC: 15, windSpeedMps: 5, solarIrradianceWm2: null })) } });
  });
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities' }).click();
  await page.getByRole('combobox', { name: 'Väderkälla (weather.*)' }).click();
  await page.getByRole('option', { name: /Testväder/ }).click();
  await page.getByRole('button', { name: 'Testa väderprognos' }).click();
  await expect(page.getByText(/2 giltiga prognospunkter/)).toBeVisible();
  await expect(page.getByText(/Solinstrålning finns i 0 punkter/)).toBeVisible();
  expect(writes).toBe(0);
});

test('temperaturgraf fungerar utan plan och ryms på mobil', async ({ page }, testInfo) => {
  await page.route('**/api/thermal/plan', route => route.fulfill({ status: 204 }));
  await page.goto('/plan');
  await expect(page.getByRole('heading', { name: 'Temperaturer och LWT', exact: true })).toBeVisible();
  await expect(page.getByText(/Senast uppmätt LWT:/)).toBeVisible();
  await expect(page.getByText(/Ingen beräknad LWT-avvikelse/)).toBeVisible();
  await page.getByRole('button', { name: '6 timmar', exact: true }).click();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('temperatures.png'), fullPage: true });
});
