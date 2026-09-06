import { expect, test } from '@playwright/test';

test('LWT skiljer climate-skrivreglage från numerisk feedback utan att aktivera styrning', async ({ page }) => {
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.url());
  });
  const now = new Date().toISOString();
  await page.route('**/api/home-assistant/entities', route => route.fulfill({ json: [{
    entityId: 'sensor.bridge0_lwt_deviation_heating', friendlyName: 'LWT återkoppling', state: '0', unit: '°C',
    quality: 0, qualityReason: null, lastUpdatedUtc: now, receivedAtUtc: now, checkedAtUtc: now,
    validUntilUtc: new Date(Date.now() + 600_000).toISOString(), compatibleUnits: ['°C'],
  }] }));
  await page.goto('/settings');
  const writer = page.getByRole('textbox', { name: 'Tillåten LWT-avvikelse-entity', exact: true });
  await writer.fill('climate.bridge0_lwt_deviation_heating');
  await expect(page.getByRole('button', { name: 'Spara HA-anslutning', exact: true })).toBeEnabled();
  await expect(page.getByRole('switch', { name: 'Tillåt styrklienten' })).not.toBeChecked();
  await expect(page.getByText(/Välj den numeriska återkopplingssensorn separat/)).toBeVisible();
  await page.getByRole('tab', { name: 'Entities', exact: true }).click();
  const feedback = page.getByRole('combobox', { name: 'Välj p1p2 lwt-avvikelse', exact: true });
  await feedback.fill('LWT återkoppling');
  await page.getByRole('option', { name: /LWT återkoppling.*sensor.bridge0_lwt_deviation_heating/ }).click();
  await expect(feedback).toHaveValue('LWT återkoppling · sensor.bridge0_lwt_deviation_heating');
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  expect(mutations).toEqual([]);
});
