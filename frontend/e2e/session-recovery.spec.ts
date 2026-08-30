import { expect, test } from '@playwright/test';

test('sessionsfel kan återhämtas utan att exponera data eller ändra styrningen', async ({ page }, testInfo) => {
  let available = false;
  let sessionRequests = 0;
  const otherApiRequests: string[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if ((path.startsWith('/api/') && path !== '/api/session') || path.startsWith('/auth/')) {
      otherApiRequests.push(`${request.method()} ${path}`);
    }
  });
  await page.route('**/api/session', route => {
    sessionRequests += 1;
    return route.fulfill(available ? {
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ authenticated: false, userId: null, isAdmin: false, csrfToken: 'test-csrf' }),
    } : {
      status: 503,
      contentType: 'text/html',
      body: '<html>Proxy failure: internal-server-detail</html>',
    });
  });

  await page.goto('/settings');
  await expect(page.getByRole('heading', { name: 'Inloggningen kunde inte kontrolleras' })).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Inga anläggningsuppgifter visas.');
  await expect(page.getByText(/internal-server-detail/)).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Styrsystemets status' })).toHaveCount(0);
  expect(otherApiRequests).toEqual([]);
  const dimensions = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
  expect(dimensions.scroll).toBe(dimensions.client);
  await testInfo.attach('sessionsfel', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });

  const requestsBeforeRetry = sessionRequests;
  available = true;
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Försök igen' })).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(page.getByRole('heading', { name: 'Logga in för att fortsätta' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Logga in med Daikin' })).toBeEnabled();
  await expect(page).toHaveURL(/\/settings$/);
  expect(sessionRequests).toBe(requestsBeforeRetry + 1);
  expect(otherApiRequests).toEqual([]);
});
