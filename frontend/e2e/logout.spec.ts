import { expect, test } from '@playwright/test';

test('utloggningsfel förklaras och ett lyckat återförsök döljer kontots vyer', async ({ page }, testInfo) => {
  let authenticated = true;
  let logoutCalls = 0;
  const mutations: string[] = [];
  const csrfHeaders: (string | undefined)[] = [];
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/api/') && request.method() !== 'GET') mutations.push(`${request.method()} ${path}`);
  });
  await page.route('**/api/session', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ authenticated, userId: authenticated ? 'default' : null, isAdmin: authenticated, csrfToken: 'test-session-csrf' }),
  }));
  await page.route('**/api/session/logout', route => {
    logoutCalls += 1;
    csrfHeaders.push(route.request().headers()['x-csrf-token']);
    if (logoutCalls === 1) return route.fulfill({ status: 503, contentType: 'text/html', body: 'internal-logout-server-detail' });
    authenticated = false;
    return route.fulfill({ status: 204 });
  });

  await page.goto('/rooms');
  await expect(page.getByRole('region', { name: 'Styrsystemets status' })).toBeVisible();
  await page.getByRole('button', { name: /^Logga ut/ }).click();

  await expect(page.getByRole('alert').filter({ hasText: 'Utloggningen kunde inte bekräftas' })).toBeVisible();
  await expect(page.getByText(/Du kan fortfarande vara inloggad/)).toBeVisible();
  await expect(page.getByText(/internal-logout-server-detail/)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Logga in för att fortsätta' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^Logga ut/ })).toBeEnabled();
  const dimensions = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
  expect(dimensions.scroll).toBe(dimensions.client);
  await testInfo.attach('utloggningsfel', { body: await page.screenshot(), contentType: 'image/png' });

  await page.getByRole('button', { name: /^Logga ut/ }).click();
  await expect(page.getByRole('heading', { name: 'Logga in för att fortsätta' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Styrsystemets status' })).toHaveCount(0);
  await expect(page).toHaveURL(/\/$/);
  expect(mutations).toEqual(['POST /api/session/logout', 'POST /api/session/logout']);
  expect(csrfHeaders).toEqual(['test-session-csrf', 'test-session-csrf']);

  await page.goBack();
  await expect(page.getByRole('heading', { name: 'Logga in för att fortsätta' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Styrsystemets status' })).toHaveCount(0);
});
