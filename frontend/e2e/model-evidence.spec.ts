import { expect, test } from '@playwright/test';

test('modellvyn skiljer modellbevis från aktiv styrning och återhämtar sig säkert från läsfel', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const mutations: string[] = [];
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.method()); });
  let state: 'unproven' | 'valid' | 'error' = 'unproven';
  await page.route('**/api/thermal/models', async route => {
    if (state === 'error') { await route.fulfill({ status: 503, json: { error: 'private-model-detail' } }); return; }
    await route.fulfill({ json: [{ id: 4, modelType: '2R2C', isActive: true, createdAtUtc: '2026-08-30T20:00:00Z', trainingFromUtc: '2026-08-01T00:00:00Z', trainingToUtc: '2026-08-30T00:00:00Z', parametersJson: '{}', metricsJson: '{}',
      provenance: { verifiable: true, algorithmVersion: 'grey-box-2r2c-v1', selectionVersion: 'thermal-validated-history-v1',
        selectionFromUtc: '2026-07-01T00:00:00Z', selectionToUtc: '2026-08-30T00:00:00Z', observationCount: 2000, trainingSamples: 1600, validationSamples: 400 },
      validation: { passed: state === 'valid', status: state === 'valid' ? 'Validated' : 'Unproven',
        reason: state === 'valid' ? 'Hela tvåtimmars- och dygnsfönster på undanhållen data klarar kraven.' : 'Den äldre modellen saknar verifierbart valideringsunderlag. Träna om modellen; en aktivmarkering räcker inte.',
        checkedAtUtc: new Date().toISOString(), twoHourMaeC: state === 'valid' ? .1 : null, dayMaeC: state === 'valid' ? .2 : null,
        copMae: null, twoHourValidationWindows: state === 'valid' ? 126 : null, dayValidationWindows: state === 'valid' ? 4 : null } }] });
  });
  await page.goto('/model');
  await expect(page.getByRole('heading', { name: 'Modell', exact: true })).toBeVisible();
  await expect(page.getByText('Husmodell: ej verifierad')).toBeVisible();
  await expect(page.getByText('Ej verifierad · aktivmarkering')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => window.innerWidth));
  await testInfo.attach('model-underlag-saknas', { body: await page.screenshot({ path: testInfo.outputPath('model-underlag-saknas.png'), fullPage: true }), contentType: 'image/png' });

  state = 'valid';
  await page.getByRole('button', { name: 'Hämta underlag igen' }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('Husmodell: validerad')).toBeVisible();
  await expect(page.getByText('0,20 °C')).toBeVisible();
  await expect(page.getByText(/4 hela 24-timmarsfönster/)).toBeVisible();
  await expect(page.getByText(/En validerad modell är inte ett godkännande av aktiv styrning/)).toBeVisible();
  await expect(page.getByText(/Träningsunderlag: spårbart · 2[  ]000 valda mätpunkter/)).toBeVisible();
  await expect(page.getByText(/Spårbart källurval · 2[  ]000 mätpunkter/)).toBeVisible();
  await page.getByRole('button', { name: /Avancerat: husmodell och rumskalibrering/ }).click();
  await expect(page.getByText('Versionsbundet träningsunderlag')).toBeVisible();
  await expect(page.getByText(/Algoritm: grey-box-2r2c-v1/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => window.innerWidth));
  await testInfo.attach('model-validerat-underlag', { body: await page.screenshot({ path: testInfo.outputPath('model-validerat-underlag.png'), fullPage: true }), contentType: 'image/png' });

  state = 'error';
  await page.getByRole('button', { name: 'Hämta underlag igen' }).click();
  await expect(page.getByText(/Modellunderlaget kunde inte hämtas/)).toBeVisible({ timeout: 15_000 });
  await expect(page.getByText('Husmodell: validerad')).toHaveCount(0);
  await expect(page.getByText('2R2C · version 4')).toHaveCount(0);
  await expect(page.getByText(/private-model-detail/)).toHaveCount(0);
  state = 'unproven';
  await page.getByRole('button', { name: 'Hämta underlag igen' }).click();
  await expect(page.getByText('Husmodell: ej verifierad')).toBeVisible();
  expect(mutations).toEqual([]);
});
