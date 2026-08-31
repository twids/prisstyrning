import { expect, test } from '@playwright/test';

test('status döljer cache efter hämtningsfel och visar inte okända enumvärden som säkra', async ({ page }, testInfo) => {
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') mutations.push(request.method());
  });
  let responseKind: 'valid' | 'error' | 'unknown' | 'stale' = 'valid';
  await page.route('**/api/thermal/status', route => {
    if (responseKind === 'error') return route.fulfill({ status: 503, json: { error: 'Synthetic unavailability' } });
    return route.fulfill({ json: {
      mode: 2, dhwWriter: 0, lastTelemetryUtc: new Date(Date.now() - (responseKind === 'stale' ? 11 : 1) * 60_000).toISOString(),
      overallDataQuality: responseKind === 'unknown' ? 99 : 0, emhassAvailable: false,
      planCreatedUtc: null, planAgeMinutes: null, currentLwtDeviationC: .5,
      fallbackReason: null, nextControlEventUtc: null, manualOverride: false,
    } });
  });

  await page.goto('/rooms');
  const status = page.getByRole('region', { name: 'Styrsystemets status' });
  await expect(status.getByText('LWT aktiv', { exact: true })).toBeVisible();
  await expect(status.getByText('Giltig', { exact: true })).toBeVisible();
  const rollback = status.getByRole('button', { name: 'Rollback' });
  await expect(rollback).toBeInViewport();
  if (testInfo.project.name === 'mobile') {
    await page.setViewportSize({ width: 320, height: 860 });
    await expect(rollback).toBeInViewport();
  }
  await testInfo.attach('aktiv-status-rollback', { body: await status.screenshot(), contentType: 'image/png' });

  responseKind = 'error';
  // Keep React Query's last successful value cached, then request a normal refresh.
  await page.evaluate(() => {
    window.dispatchEvent(new Event('offline'));
    window.dispatchEvent(new Event('online'));
  });
  await expect(status.getByRole('alert')).toContainText('Status kunde inte hämtas', { timeout: 15_000 });
  await expect(status.getByText('Giltig', { exact: true })).toHaveCount(0);
  await expect(status.getByRole('button', { name: 'Byt läge' })).toHaveCount(0);
  await rollback.focus();
  await expect(rollback).toBeInViewport();
  await expect(rollback).toBeFocused();
  await testInfo.attach('status-hamtningsfel', { body: await status.screenshot(), contentType: 'image/png' });

  responseKind = 'unknown';
  await page.reload();
  await expect(status.getByRole('alert')).toContainText('Aktuellt driftläge och datakvalitet kan inte bekräftas', { timeout: 15_000 });
  await expect(status.getByText('Giltig', { exact: true })).toHaveCount(0);

  responseKind = 'stale';
  await page.reload();
  await expect(status.getByText('Gammal', { exact: true })).toBeVisible();
  await expect(status.getByText(/äldre än tio minuter/)).toBeVisible();
  await expect(status.getByText('Giltig', { exact: true })).toHaveCount(0);
  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBe(await page.evaluate(() => document.documentElement.clientWidth));
  expect(mutations).toEqual([]);
});
