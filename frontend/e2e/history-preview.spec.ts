import { expect, test } from '@playwright/test';

test('historikkontroll visar luckor utan att importera eller ändra styrningen', async ({ page }, testInfo) => {
  const writes: string[] = [];
  page.on('request', request => {
    if (request.url().includes('/api/') && request.method() !== 'GET') writes.push(new URL(request.url()).pathname);
  });
  await page.route('**/api/home-assistant/history-preview', route => {
    const interval = route.request().postDataJSON();
    return route.fulfill({ json: { ...interval, expectedSamples: 100, existingSamples: 5,
      sensors: [{ entityId: 'sensor.room', purpose: 'Rum: Vardagsrum', valid: 65, stale: 30, invalid: 3, unavailable: 2 }] } });
  });
  await page.goto('/settings');
  await page.getByRole('button', { name: 'Kontrollera historik' }).click();
  await expect(page.getByText(/Giltiga: 65. Osäker rapportålder: 30. Ogiltiga\/exkluderade: 3. Saknas: 2/)).toBeVisible();
  await expect(page.getByText(/Ingen modell eller Shadow-period är godkänd/)).toBeVisible();
  expect(writes).toEqual(['/api/home-assistant/history-preview']);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('history-preview.png'), fullPage: true });
  await page.getByLabel('Från', { exact: true }).fill('2026-01-01T00:00');
  await expect(page.getByRole('button', { name: 'Kontrollera historik' })).toBeDisabled();
  await expect(page.getByText(/Giltiga: 65/)).toHaveCount(0);
});
