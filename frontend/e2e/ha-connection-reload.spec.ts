import { expect, test } from '@playwright/test';

test('sparad HA-ändring visar omladdning, avvisar sent gammalt statusbesked och återhämtar läsfel', async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET')
      mutations.push(`${request.method()} ${new URL(request.url()).pathname}`);
  });
  const now = Date.now();
  let connection = {
    baseUrl: 'https://ha.example.test', telemetryEnabled: true, controlEnabled: false,
    telemetryTokenConfigured: true, controlTokenConfigured: false, heatingDeviationEntityId: '', staleAfterMinutes: 10,
    updatedAtUtc: new Date(now - 60_000).toISOString(),
  };
  let status = {
    configured: true, connected: true, phase: 'Connected', configurationUpdatedAtUtc: connection.updatedAtUtc,
    lastSnapshotUtc: new Date(now - 30_000).toISOString(), lastActivityUtc: new Date(now).toISOString(), cachedEntities: 27,
  };
  const oldStatus = { ...status };
  let holdOldResponse = false;
  let oldRequested = false;
  let releaseOld: () => void = () => {};
  const oldResponseGate = new Promise<void>(resolve => { releaseOld = resolve; });
  let statusFails = false;
  await page.route('**/api/home-assistant/config', async route => {
    if (route.request().method() === 'PUT') {
      const request = route.request().postDataJSON();
      expect(request.staleAfterMinutes).toBe(15);
      expect(request.telemetryToken).toBeNull();
      expect(request.controlToken).toBeNull();
      expect(request.controlEnabled).toBe(false);
      connection = { ...connection, staleAfterMinutes: 15, updatedAtUtc: new Date().toISOString() };
      status = { ...status, connected: false, phase: 'Reloading', configurationUpdatedAtUtc: connection.updatedAtUtc, cachedEntities: 0 };
    }
    await route.fulfill({ json: connection });
  });
  await page.route('**/api/home-assistant/status', async route => {
    if (holdOldResponse) {
      holdOldResponse = false;
      oldRequested = true;
      await oldResponseGate;
      await route.fulfill({ json: oldStatus });
      return;
    }
    await route.fulfill(statusFails ? { status: 503, json: { error: 'private-status-detail' } } : { json: status });
  });

  await page.goto('/settings');
  const live = page.getByRole('region', { name: 'Liveanslutning' });
  await expect(live.getByRole('status')).toContainText('Liveansluten');
  await expect(live.getByText('27', { exact: true })).toBeVisible();
  await expect(page.getByText('Anslutning sparad', { exact: true })).toBeVisible();
  holdOldResponse = true;
  await live.getByRole('button', { name: 'Uppdatera anslutningsstatus' }).click();
  await expect.poll(() => oldRequested).toBe(true);
  await page.getByLabel('Gammal efter, minuter').fill('15');
  await expect(page.getByText(/den gamla telemetrianslutningen och dess cache töms/)).toBeVisible();
  await page.getByRole('button', { name: 'Spara HA-anslutning' }).click();
  await expect(live.getByRole('status')).toContainText('Laddar om anslutningen');
  releaseOld();
  await expect(live.getByRole('status')).not.toContainText('Liveansluten');
  await expect(live.getByText('27', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/Liveanslutningen kontrolleras separat/)).toBeVisible();

  status = { ...status, phase: 'Synchronizing' };
  await live.getByRole('button', { name: 'Uppdatera anslutningsstatus' }).click();
  await expect(live.getByRole('status')).toContainText('Läser ny startbild');
  expect(await page.evaluate(() => document.documentElement.scrollWidth))
    .toBe(await page.evaluate(() => document.documentElement.clientWidth));
  await testInfo.attach('ha-startbild', { body: await live.screenshot({ path: testInfo.outputPath('ha-startbild.png') }), contentType: 'image/png' });

  status = { ...status, connected: true, phase: 'Connected', lastSnapshotUtc: new Date().toISOString(), cachedEntities: 31 };
  await live.getByRole('button', { name: 'Uppdatera anslutningsstatus' }).click();
  await expect(live.getByRole('status')).toContainText('Liveansluten');
  await expect(live.getByText('31', { exact: true })).toBeVisible();
  statusFails = true;
  await live.getByRole('button', { name: 'Uppdatera anslutningsstatus' }).click();
  await expect(live.getByRole('status')).toContainText('Status kunde inte hämtas', { timeout: 15_000 });
  await expect(live.getByText('31', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/private-status-detail/)).toHaveCount(0);
  await testInfo.attach('ha-statusfel', { body: await live.screenshot({ path: testInfo.outputPath('ha-statusfel.png') }), contentType: 'image/png' });

  statusFails = false;
  const retry = live.getByRole('button', { name: 'Uppdatera anslutningsstatus' });
  await retry.focus();
  await page.keyboard.press('Enter');
  await expect(live.getByRole('status')).toContainText('Liveansluten');
  await expect(live.getByText('31', { exact: true })).toBeVisible();
  expect(mutations).toEqual(['PUT /api/home-assistant/config']);
});
