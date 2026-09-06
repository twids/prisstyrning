import { expect, test } from '@playwright/test';

test('Legacy visar ansluten EMHASS utan att starta optimering eller ändra läge', async ({ page }, testInfo) => {
  const writes: string[] = [];
  page.on('request', request => {
    if (request.url().includes('/api/') && request.method() !== 'GET') writes.push(request.url());
  });
  await page.route('**/api/thermal/status', async route => {
    const original = await route.fetch();
    const status = await original.json();
    await route.fulfill({ json: { ...status, mode: 'Legacy', dhwWriter: 'Legacy',
      emhassEnabled: true, emhassAvailable: false, planCreatedUtc: null, planAgeMinutes: null,
      emhassConnection: { reachable: true, checkedUtc: new Date().toISOString() } } });
  });
  await page.goto('/');
  await expect(page.getByText('EMHASS ansluten', { exact: true })).toBeVisible();
  await expect(page.getByText(/Ingen optimering körs i Legacy; den befintliga varmvattenstyrningen fortsätter/)).toBeVisible();
  await expect(page.getByText('EMHASS ej verifierad', { exact: true })).toHaveCount(0);
  expect(writes).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('emhass-legacy.png'), fullPage: true });
});
