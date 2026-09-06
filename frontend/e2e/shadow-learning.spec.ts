import { expect, test } from '@playwright/test';

test('bergvärme väljs uttryckligen utan hjälpsensor eller automatisk sparning', async ({ page }) => {
  const mutations: string[] = [];
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/') && r.method() !== 'GET') mutations.push(r.url()); });
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities', exact: true }).click();
  await page.getByRole('switch', { name: 'Anläggningen har ingen avfrostning (till exempel bergvärme)' }).check();
  await expect(page.getByRole('combobox', { name: 'Välj avfrostning aktiv', exact: true })).toHaveCount(0);
  await expect(page.getByText(/Ej tillämpligt — en uttrycklig anläggningsuppgift/)).toBeVisible();
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  expect(mutations).toEqual([]);
});

test('Shadow visar prognos utfärdad före utfall utan påstådd LWT-styrning', async ({ page }) => {
  const now = new Date().toISOString();
  await page.route('**/api/thermal/learning', r => r.fulfill({ json: [{ id: 3, issuedAtUtc: now, twoHourErrorC: null, dayErrorC: null,
    learning: { stage: 'Persistence', samples: 10, heatingSamples: 0, minimumOutsideC: 15, maximumOutsideC: 19,
      trendCPerHour: 0, heldOutMaeC: null, persistenceMaeC: null,
      forecast: [{ timestampUtc: now, predictedC: 21, actualC: null }] } }] }));
  await page.goto('/model');
  await expect(page.getByText('Preliminär baslinje: oförändrad temperatur')).toBeVisible();
  await expect(page.getByRole('img', { name: /Temperaturprognos och uppmätt utfall/ })).toBeVisible();
  await expect(page.getByText(/Värmerespons och LWT-förslag är ännu inte verifierade/)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});
