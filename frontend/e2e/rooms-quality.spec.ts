import { expect, test } from '@playwright/test';

test('rum skiljer aktuella mätningar från reservvärden och daterad händelsehistorik', async ({ page }, testInfo) => {
  const now = Date.now();
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.method() + ' ' + new URL(request.url()).pathname);
  });
  await page.route('**/api/thermal/history*', route => route.fulfill({ json: [{
    id: 1, userId: 'default', timestampUtc: new Date(now - 60_000).toISOString(),
    roomTemperaturesJson: JSON.stringify({ 'sensor.vardagsrum_temperature': 21.4, 'sensor.sovrum_temperature': 20.4, 'sensor.kontor_temperature': 21.5 }),
    qualityJson: JSON.stringify({ rooms: {
      'sensor.vardagsrum_temperature': { Quality: 2, Excluded: true },
      'sensor.sovrum_temperature': { quality: 'Valid', excluded: false },
      'sensor.kontor_temperature': { Quality: 0, Excluded: false },
    } }),
  }] }));
  await page.route('**/api/thermal/events*', route => route.fulfill({ json: [
    { id: 1, timestampUtc: new Date(now - 3 * 3_600_000).toISOString(), severity: 'ActionRequired', category: 'DataQuality', message: 'Givaren har exkluderats efter tre felaktiga mätningar.' },
    { id: 2, timestampUtc: new Date(now - 2 * 3_600_000).toISOString(), severity: 'Information', category: 'DataQuality', message: 'Givaren används igen efter tre giltiga mätningar.' },
    { id: 3, timestampUtc: new Date(now - 3_600_000).toISOString(), severity: 'Warning', category: 'RoomBalance', message: 'Kontrollera injusteringen i sovrummet.' },
  ] }));

  await page.goto('/rooms');

  const fallback = page.getByRole('article', { name: 'Vardagsrum', exact: true });
  await expect(fallback.getByText('Sparat reservvärde')).toBeVisible();
  await expect(fallback.getByText('Exkluderad', { exact: true })).toBeVisible();
  await expect(fallback.getByText('Okänd', { exact: true })).toBeVisible();
  const cold = page.getByRole('article', { name: 'Sovrum', exact: true });
  await expect(cold.getByText('Giltig', { exact: true })).toBeVisible();
  await expect(cold.getByText(/Under komfortgränsen/)).toBeVisible();
  await expect(page.getByText(/2 av 3 aktiverade rum/)).toBeVisible();

  const history = page.getByRole('region', { name: 'Rum- och givarhistorik' });
  await expect(history.getByText(/inte en lista över aktiva larm/)).toBeVisible();
  for (const label of ['Information', 'Varning', 'Åtgärd krävs']) await expect(history.getByText(label, { exact: true })).toBeVisible();
  await expect(history.getByRole('alert')).toHaveCount(0);
  await expect(history.locator('time')).toHaveCount(3);
  for (const timestamp of await history.locator('time').all()) await expect(timestamp).toHaveAttribute('datetime', /Z$/);
  await testInfo.attach('rumskvalitet', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });

  if (testInfo.project.name === 'mobile') {
    await page.setViewportSize({ width: 320, height: 860 });
    await expect(fallback.getByText('Sparat reservvärde')).toBeVisible();
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(await page.evaluate(() => document.documentElement.clientWidth));
  for (const label of await page.locator('article dl dt').evaluateAll(labels => labels.map(element => ({ text: element.textContent, client: element.clientWidth, scroll: element.scrollWidth })))) {
    expect(label.scroll, 'Rumskortets etikett ska rymmas: ' + label.text).toBeLessThanOrEqual(label.client + 1);
  }
  const logLink = history.getByRole('link', { name: 'Öppna hela händelseloggen' });
  await logLink.focus();
  await expect(logLink).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/events$/);
  expect(mutations).toEqual([]);
});
