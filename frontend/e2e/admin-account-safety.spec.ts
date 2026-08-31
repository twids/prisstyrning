import { expect, test } from '@playwright/test';

test('admin förklarar raderingsspärren och döljer gamla kontouppgifter vid fel', async ({ page }, testInfo) => {
  const mutations: string[] = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.startsWith('/api/') && request.method() !== 'GET') {
      mutations.push(`${request.method()} ${new URL(request.url()).pathname}`);
    }
  });
  const account = {
    userId: 'default', zone: 'SE3', settings: { ComfortHours: 3, TurnOffPercentile: .9, MaxComfortGapHours: 28 },
    daikinAuthorized: true, daikinExpiresAtUtc: null, daikinSubject: null,
    hasScheduleHistory: true, scheduleCount: 42, lastScheduleDate: '2026-08-30T23:35:03Z',
    isAdmin: true, isCurrentUser: true, hasHangfireAccess: true,
  };
  let usersFail = false;
  let statusFail = false;
  await page.route('**/api/admin/users', route => usersFail
    ? route.fulfill({ status: 503, json: { error: 'Synthetic private-user-detail' } })
    : route.fulfill({ json: { users: [account, {
      ...account, userId: 'older-account-without-credential', isCurrentUser: false,
      isAdmin: false, hasHangfireAccess: false, daikinAuthorized: false,
    }] } }));
  await page.route('**/api/admin/status', route => statusFail
    ? route.fulfill({ status: 503, json: { error: 'Synthetic private-admin-detail' } })
    : route.fulfill({ json: { isAdmin: true, userId: 'default' } }));

  await page.goto('/admin');
  const list = page.getByRole('region', { name: 'Användarlista' });
  await expect(list).toBeVisible();
  await expect(page.getByRole('note')).toContainText('Kontoradering är tillfälligt spärrad');
  await expect(page.getByRole('note')).toContainText('befintlig schemastyrning fortsätter');
  const deletionButtons = page.getByRole('button', { name: /^Radering spärrad för/ });
  await expect(deletionButtons).toHaveCount(2);
  await expect(deletionButtons.nth(0)).toBeDisabled();
  await expect(deletionButtons.nth(1)).toBeDisabled();
  await expect(page.getByRole('dialog')).toHaveCount(0);

  if (testInfo.project.name === 'mobile') await page.setViewportSize({ width: 320, height: 860 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth))
    .toBe(await page.evaluate(() => document.documentElement.clientWidth));
  await list.focus();
  await expect(list).toBeFocused();
  if (testInfo.project.name === 'mobile') {
    await page.keyboard.press('ArrowRight');
    await expect.poll(() => list.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  await testInfo.attach('admin-raderingssparr', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });

  usersFail = true;
  await page.evaluate(() => {
    window.dispatchEvent(new Event('offline'));
    window.dispatchEvent(new Event('online'));
  });
  await expect(page.getByRole('alert')).toContainText('Användarlistan kunde inte hämtas', { timeout: 15_000 });
  await expect(list).toHaveCount(0);
  await expect(page.getByText('older-account-without-credential', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/private-user-detail/)).toHaveCount(0);
  await testInfo.attach('admin-listfel', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
  usersFail = false;
  await page.getByRole('button', { name: 'Försök igen' }).click();
  await expect(list).toBeVisible();
  await expect(deletionButtons.nth(1)).toBeDisabled();

  statusFail = true;
  await page.evaluate(() => {
    window.dispatchEvent(new Event('offline'));
    window.dispatchEvent(new Event('online'));
  });
  await expect(page.getByRole('alert')).toContainText('Adminbehörigheten kunde inte kontrolleras', { timeout: 15_000 });
  await expect(list).toHaveCount(0);
  await expect(page.getByRole('switch')).toHaveCount(0);
  await expect(page.getByLabel('Lösenord', { exact: true })).toHaveCount(0);
  await expect(page.getByText(/private-admin-detail/)).toHaveCount(0);
  await testInfo.attach('admin-behorighetsfel', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
  statusFail = false;
  await page.getByRole('button', { name: 'Försök igen' }).click();
  await expect(list).toBeVisible();
  await expect(deletionButtons.nth(1)).toBeDisabled();
  expect(mutations).toEqual([]);
});
