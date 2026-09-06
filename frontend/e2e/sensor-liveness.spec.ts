import { expect, test } from '@playwright/test';

test('separat livstecken kan förhandsgranskas utan att spara eller aktivera styrning', async ({ page }, testInfo) => {
  const writes: string[] = [];
  page.on('request', request => { if (request.url().includes('/api/') && request.method() !== 'GET') writes.push(new URL(request.url()).pathname); });
  await page.route('**/api/home-assistant/freshness-preview', route => {
    const request = route.request().postDataJSON();
    expect(request.role).toBe('room');
    expect(request.freshnessAttribute).toBe('last_seen');
    return route.fulfill({ json: { quality: 0, reason: 'Oförändrat värde med verifierat separat livstecken.', checkedAtUtc: new Date().toISOString(), valueUpdatedUtc: new Date(Date.now() - 7200000).toISOString(), livenessUtc: new Date().toISOString() } });
  });
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Rum', exact: true }).click();
  await page.getByRole('button', { name: /Avancerat: rapportering för/ }).first().click();
  const attribute = page.getByLabel(/Livstecknets attribut för/).first();
  await attribute.fill('last_seen');
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  await page.getByRole('button', { name: /Testa rapportålder för/ }).first().click();
  await expect(page.getByText('Oförändrat värde med verifierat separat livstecken.')).toBeVisible();
  expect(writes).toEqual(['/api/home-assistant/freshness-preview']);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('sensor-liveness.png'), fullPage: true });
  await attribute.fill('different_attribute');
  await expect(page.getByText('Oförändrat värde med verifierat separat livstecken.')).toHaveCount(0);
});
