import { expect, test } from '@playwright/test';

test('lägesguiden återkallar ett gammalt godkännande och förklarar felaktiga sensorer utan att byta läge', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET')
      mutations.push(`${request.method()} ${new URL(request.url()).pathname}`);
  });
  let result: 'valid' | 'error' | 'invalid' = 'valid';
  await page.route('**/api/thermal/readiness?*', async route => {
    if (result === 'error') {
      await route.fulfill({ status: 503, json: { error: 'private-readiness-detail' } });
      return;
    }
    await route.fulfill({ json: {
      targetMode: 2,
      ready: result === 'valid',
      checks: [{
        key: 'telemetry-quality',
        requirement: 'Kritiska rum och obligatoriska värmegivare har giltig liveinsamling',
        passed: result === 'valid',
        action: result === 'valid' ? 'Ingen åtgärd krävs.' : 'Kontrollera givarens enhet och tidsstämpel i Rum/Inställningar. Ersättningsvärden är inte godkänd liveinsamling.',
        severity: result === 'valid' ? 'Information' : 'ActionRequired',
      }],
    } });
  });

  await page.goto('/');
  await page.getByRole('button', { name: 'Byt läge' }).click();
  const dialog = page.getByRole('dialog', { name: 'Guidad ändring av driftläge' });
  await dialog.getByRole('button', { name: 'Fortsätt' }).click();
  await expect(dialog.getByText('1 av 1 krav är godkända.')).toBeVisible();
  await dialog.getByRole('button', { name: 'Fortsätt' }).click();
  await expect(dialog.getByRole('button', { name: 'Aktivera LWT aktiv' })).toBeEnabled();

  result = 'error';
  await dialog.getByRole('button', { name: 'Kontrollera kraven igen' }).click();
  await expect(dialog.getByText(/Kraven kunde inte kontrolleras/)).toBeVisible({ timeout: 15_000 });
  await expect(dialog.getByRole('button', { name: 'Aktivera LWT aktiv' })).toBeDisabled();
  await expect(page.getByText(/private-readiness-detail/)).toHaveCount(0);
  if (testInfo.project.name === 'mobile') await expect(dialog.getByText('Steg 3 av 3: Bekräfta ansvar')).toBeVisible();
  const buttonWidths = await dialog.getByRole('button').evaluateAll(buttons => buttons.map(button => ({ client: button.clientWidth, scroll: button.scrollWidth })));
  for (const width of buttonWidths) expect(width.scroll).toBeLessThanOrEqual(width.client);
  await testInfo.attach('readiness-lasfel', { body: await dialog.screenshot({ path: testInfo.outputPath('readiness-lasfel.png') }), contentType: 'image/png' });

  result = 'invalid';
  await dialog.getByRole('button', { name: 'Kontrollera kraven igen' }).focus();
  await page.keyboard.press('Enter');
  await dialog.getByRole('button', { name: 'Tillbaka' }).click();
  await expect(dialog.getByText('0 av 1 krav är godkända.')).toBeVisible();
  await expect(dialog.getByText('Åtgärd krävs', { exact: true })).toBeVisible();
  await expect(dialog.getByText(/Kontrollera givarens enhet och tidsstämpel/)).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
  expect(await dialog.evaluate(element => element.scrollWidth)).toBeLessThanOrEqual(await dialog.evaluate(element => element.clientWidth));
  await testInfo.attach('readiness-sensorfel', { body: await dialog.screenshot({ path: testInfo.outputPath('readiness-sensorfel.png') }), contentType: 'image/png' });
  expect(mutations).toEqual([]);
});
