import { expect, test } from '@playwright/test';

test('modellavvisning visas som historik med läsande återhämtning på desktop och mobil', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const mutations: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.method()); });
  const reason = 'COP-modellen behöver tränas om. Senaste giltiga plan används högst 60 minuter.';
  let fail = false;
  await page.route('**/api/thermal/events?*', async route => {
    if (fail) { await route.fulfill({ status: 503, json: { error: 'private-planning-detail' } }); return; }
    await route.fulfill({ json: [{ id: 9, timestampUtc: '2026-08-31T18:00:00Z', severity: 'Warning', category: 'Optimizer', message: reason, detailsJson: '{}' }] });
  });
  await page.goto('/events');
  await expect(page.getByRole('heading', { name: 'Händelser', exact: true })).toBeVisible();
  await expect(page.getByText(reason)).toBeVisible();
  await expect(page.getByRole('listitem').getByText('Varning', { exact: true })).toBeVisible();
  await expect(page.getByText(/inte en lista över aktiva larm/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => window.innerWidth));
  await testInfo.attach('modellavvisning-historik', { body: await page.screenshot({ path: testInfo.outputPath('modellavvisning-historik.png'), fullPage: true }), contentType: 'image/png' });

  fail = true;
  await page.getByRole('button', { name: 'Hämta historik igen' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText(/Visar tidigare hämtade händelser/)).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText(reason)).toBeVisible();
  await expect(page.getByText('private-planning-detail')).toHaveCount(0);
  fail = false;
  await page.getByRole('button', { name: 'Hämta historik igen' }).click();
  await expect(page.getByText(/Visar tidigare hämtade händelser/)).toHaveCount(0);
  expect(mutations).toEqual([]);
});
