import { expect, test } from '@playwright/test';

test('Shadow kan bekräftas med datavarningar utan att begära aktiv styrning', async ({ page }, testInfo) => {
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const commands: unknown[] = [];
  await page.route('**/api/thermal/status', async route => {
    const response = await route.fetch();
    await route.fulfill({ json: { ...await response.json(), mode: 0, dhwWriter: 0 } });
  });
  await page.route('**/api/thermal/readiness?*', route => route.fulfill({ json: {
    targetMode: 1, ready: true,
    checks: [
      { key: 'ha-live', requirement: 'HA är anslutet', passed: true, action: 'Ingen åtgärd krävs.', severity: 'Information' },
      { key: 'telemetry-quality', requirement: 'Aktuella givare', passed: false, action: 'Givaren har inte rapporterat på tio minuter.', severity: 'Warning' },
    ],
  } }));
  await page.route('**/api/thermal/mode', async route => {
    commands.push(route.request().postDataJSON());
    await route.fulfill({ json: { message: 'Simulerat lägesbyte' } });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Byt läge' }).click();
  const dialog = page.getByRole('dialog', { name: 'Guidad ändring av driftläge' });
  await expect(dialog.getByRole('radio', { name: /^Shadow/ })).toBeChecked();
  await dialog.getByRole('button', { name: 'Fortsätt' }).click();
  await expect(dialog.getByText('Varning – hindrar inte Shadow')).toBeVisible();
  await expect(dialog.getByText(/Legacy fortsätter styra varmvattnet/)).toBeVisible();
  await dialog.getByRole('button', { name: 'Fortsätt' }).click();
  await expect(dialog.getByText(/Shadow startas med datavarningar/)).toBeVisible();
  expect(await dialog.evaluate(element => element.scrollWidth)).toBeLessThanOrEqual(await dialog.evaluate(element => element.clientWidth));
  expect(commands).toEqual([]);
  await dialog.getByRole('button', { name: 'Aktivera Shadow' }).click();
  await expect(dialog).not.toBeVisible();
  expect(commands).toEqual([{ mode: 1, confirmed: true }]);
});
