import { expect, test } from '@playwright/test';

test('sensorval bevarar mappningar, förklarar kvalitet och återhämtar läsfel utan skrivning', async ({ page }, testInfo) => {
  test.setTimeout(45_000);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET')
      mutations.push(`${request.method()} ${new URL(request.url()).pathname}`);
  });
  const now = Date.now();
  const iso = (minutes: number) => new Date(now + minutes * 60_000).toISOString();
  const raw = {
    lastUpdatedUtc: iso(-1), receivedAtUtc: iso(0), quality: 0, qualityReason: null,
    checkedAtUtc: iso(0), validUntilUtc: iso(9),
  };
  const entities = [
    { ...raw, entityId: 'sensor.outside', friendlyName: 'Utomhus', state: 'unknown', unit: '°C', quality: 3, qualityReason: 'Home Assistant saknar ett tillgängligt värde för denna entity.', compatibleUnits: [] },
    { ...raw, entityId: 'sensor.room_fahrenheit', friendlyName: 'Rum Fahrenheit', state: '68', unit: '°F', compatibleUnits: ['°C'] },
    { ...raw, entityId: 'sensor.heat_pump_power', friendlyName: 'Värmepump effekt', state: '1500', unit: 'W', compatibleUnits: ['kW'] },
  ];
  let entitiesFail = false;
  await page.route('**/api/home-assistant/entities', route => entitiesFail
    ? route.fulfill({ status: 503, json: { error: 'Synthetic private-catalog-detail' } })
    : route.fulfill({ json: entities }));
  await page.route('**/api/thermal/config', async route => {
    const response = await route.fetch();
    const config = await response.json();
    await route.fulfill({ json: {
      ...config,
      rooms: [{ ...config.rooms[0], entityId: 'sensor.saved_room_not_in_list' }],
      entities: [{ id: 1, userId: 'default', role: 'outside_temperature', entityId: 'sensor.outside', expectedUnit: '°C', enabled: true, minimumValid: null, maximumValid: null, maximumRatePerHour: null }],
    } });
  });

  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities' }).click();
  const outside = page.getByRole('combobox', { name: 'Välj utetemperatur' });
  const outsideGroup = page.getByRole('group', { name: 'Datakälla: Välj utetemperatur' });
  await expect(outsideGroup.getByRole('status')).toContainText('Saknas');
  await expect(outsideGroup.getByText('Värde/enhet OK')).toHaveCount(0);
  await expect(page.getByText('Osparade ändringar')).toHaveCount(0);

  await outside.fill('Värmepump');
  await page.getByRole('option', { name: /Värmepump effekt/ }).click();
  await expect(outsideGroup.getByRole('status')).toContainText('Värdet kan inte läsas som °C');
  await expect(outside).toHaveAttribute('aria-invalid', 'true');
  await outside.fill('Rum Fahrenheit');
  await outside.press('ArrowDown');
  await outside.press('Enter');
  await expect(outside).toHaveValue('Rum Fahrenheit · sensor.room_fahrenheit');
  await expect(outsideGroup.getByRole('status')).toContainText('68 °F');
  await expect(outsideGroup.getByText('Värde/enhet OK')).toBeVisible();
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Konsekvens före sparande' })).toBeVisible();
  await expect(page.getByText(/aktiverar aldrig ett driftläge/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth))
    .toBe(await page.evaluate(() => document.documentElement.clientWidth));
  await outside.scrollIntoViewIfNeeded();
  await testInfo.attach('entity-varde-enhet', { body: await outsideGroup.screenshot({ path: testInfo.outputPath('entity-varde-enhet.png') }), contentType: 'image/png' });

  entitiesFail = true;
  await page.getByRole('button', { name: 'Uppdatera sensorlistan' }).click();
  await expect(page.getByRole('alert')).toContainText('Sensorlistan kunde inte uppdateras', { timeout: 15_000 });
  await expect(outside).toHaveValue('sensor.room_fahrenheit');
  await expect(outside).toBeDisabled();
  await expect(outsideGroup.getByRole('status')).not.toContainText('68 °F');
  await expect(outsideGroup.getByText('Värde/enhet OK')).toHaveCount(0);
  await expect(page.getByText(/private-catalog-detail/)).toHaveCount(0);
  await testInfo.attach('entity-lasfel', { body: await outsideGroup.screenshot({ path: testInfo.outputPath('entity-lasfel.png') }), contentType: 'image/png' });
  entitiesFail = false;
  await page.getByRole('button', { name: 'Uppdatera sensorlistan' }).click();
  await expect(outside).toBeEnabled();
  await expect(outside).toHaveValue('Rum Fahrenheit · sensor.room_fahrenheit');
  await expect(outsideGroup.getByText('Värde/enhet OK')).toBeVisible();

  await page.getByRole('tab', { name: 'Rum', exact: true }).click();
  const room = page.getByRole('combobox', { name: /Temperaturentity för Vardagsrum/ });
  const roomGroup = page.getByRole('group', { name: 'Datakälla: Temperaturentity för Vardagsrum' });
  await expect(room).toHaveValue('sensor.saved_room_not_in_list');
  await expect(roomGroup.getByRole('status')).toContainText('Mappningen är kvar');
  await room.fill('Rum Fahrenheit');
  await page.getByRole('option', { name: /Rum Fahrenheit/ }).click();
  await expect(roomGroup.getByText('Värde/enhet OK')).toBeVisible();
  await expect(roomGroup.getByRole('status')).toContainText('68 °F');
  expect(await page.evaluate(() => document.documentElement.scrollWidth))
    .toBe(await page.evaluate(() => document.documentElement.clientWidth));
  await room.scrollIntoViewIfNeeded();
  await testInfo.attach('rum-sensorval', { body: await roomGroup.screenshot({ path: testInfo.outputPath('rum-sensorval.png') }), contentType: 'image/png' });
  expect(mutations).toEqual([]);
});
