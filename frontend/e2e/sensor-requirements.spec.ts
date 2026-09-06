import { expect, test } from '@playwright/test';

test('sensorval förklarar krav och nytta utan att ändra konfiguration', async ({ page }, testInfo) => {
  const writes: string[] = [];
  page.on('request', request => {
    if (request.url().includes('/api/') && ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method())) writes.push(request.url());
  });
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities' }).click();
  const summary = page.getByRole('region', { name: 'Vad behöver fyllas i?' });
  await expect(summary).toBeVisible();
  await expect(summary).toContainText('av 12 nödvändiga datakällor');
  await expect(summary).toContainText('inte ett godkännande av mätdata, modell eller aktiv styrning');
  await expect(page.getByText('Inför aktiv LWT-styrning', { exact: true })).toBeVisible();
  await expect(page.getByText('Alternativ COP-källa', { exact: true })).toBeVisible();
  await expect(page.getByText('Valfri förbättring', { exact: true })).toHaveCount(2);
  const dimensions = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
  expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.client + 1);
  expect(writes).toEqual([]);
  await summary.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('sensor-requirements.png') });
});
