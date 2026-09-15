import { expect, test } from '@playwright/test';

test('försiktig start kräver uttryckligt testgodkännande och ryms på mobil', async ({ page }, testInfo) => {
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const requests: unknown[] = [];
  await page.route('**/api/thermal/startup', route => route.fulfill({ json: {
    readyToCommission: true, phase: 'NotCommissioned', conservativeEnabled: false,
    safetyChecks: [{ key: 'live', requirement: 'Verifierad aktuell telemetri', passed: true, action: 'Godkänt.', severity: 'Information' }],
    optimizationChecks: [],
  } }));
  await page.route('**/api/thermal/mode', async route => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({ json: { message: 'Simulerat inkopplingstest' } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Försiktig start och inlärning' }).click();
  const dialog = page.getByRole('dialog', { name: 'Försiktig start och inlärning' });
  await expect(dialog.getByRole('heading', { name: '1. Säker att starta' })).toBeVisible();
  const submit = dialog.getByRole('button', { name: 'Verifiera inkoppling och starta' });
  await expect(submit).toBeDisabled();
  const checks = dialog.getByRole('checkbox');
  await expect(checks).toHaveCount(3);
  await checks.nth(0).focus();
  for (let i = 0; i < 3; i++) {
    await expect(checks.nth(i)).toBeFocused();
    await page.keyboard.press('Space');
    if (i < 2) await page.keyboard.press('Tab');
  }
  expect(requests).toEqual([]);
  await expect(submit).toBeEnabled();
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('startup-guide.png'), fullPage: false });
  await submit.click();
  await expect(dialog.getByText(/Inkoppling och nollställning verifierades/)).toBeVisible();
  expect(requests).toEqual([{ mode: 2, confirmed: true, conservativeStart: true,
    weatherCurveModeConfirmed: true, independentFallbackConfirmed: true }]);
});

test('försiktigt förslag visas utan modellplan och utan skrivningar', async ({ page }, testInfo) => {
  let writes = 0;
  await page.route('**/api/thermal/mode', route => { writes++; return route.fulfill({ status: 500 }); });
  await page.route('**/api/thermal/plan', route => route.fulfill({ status: 204 }));
  await page.route('**/api/thermal/startup/preview', route => route.fulfill({ json: {
    calculatedAtUtc: new Date().toISOString(), observedDeviationC: 0, suggestedDeviationC: .5,
    reason: 'Skrivfri ögonblicksbild utan integraldel eller prisbidrag.', simulationOnly: true,
  } }));
  await page.goto('/plan');
  await expect(page.getByText(/Försiktigt förslag nu: 0,5 °C/)).toBeVisible();
  await expect(page.getByText(/inte ett utfört kommando eller en framtidsprognos/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('conservative-preview.png'), fullPage: true });
  expect(writes).toBe(0);
});
