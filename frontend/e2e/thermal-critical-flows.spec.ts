import { expect, test } from '@playwright/test';

test('översikt leder till en förklarad shadowplan', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === 'mobile', 'Samma informationsflöde täcks i desktopprojektet.');
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Överblick utan överraskningar' })).toBeVisible();
  await expect(page.getByText('Legacy', { exact: true }).first()).toBeVisible();
  await page.getByRole('link', { name: 'Öppna 48-timmarsplanen' }).click();
  await expect(page).toHaveURL(/\/plan$/);
  await expect(page.getByText('Shadow – prickad markör')).toBeVisible();
  await expect(page.getByText('Varför just nu?')).toBeVisible();
  await expect(page.getByRole('heading', { name: /EMHASS minimerar kostnaden inom komfortbandet/ })).toBeVisible();
});

test('driftlägesguiden blockerar LWT när ett krav saknas', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === 'mobile', 'Samma säkerhetsflöde täcks i desktopprojektet.');
  await page.goto('/');
  await page.getByRole('button', { name: 'Byt läge' }).click();
  await expect(page.getByRole('radio', { name: /LWT aktiv/i })).toBeChecked();
  await expect(page.getByRole('radio', { name: /Fullt aktiv/i })).toBeDisabled();
  await page.getByRole('button', { name: 'Fortsätt' }).click();
  await expect(page.getByText('4 av 5 krav är godkända.')).toBeVisible();
  const dialog = page.getByRole('dialog', { name: 'Guidad ändring av driftläge' });
  await expect(dialog.getByText(/Genomför minst sju verkliga uppvärmningsdygn/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Fortsätt' })).toBeDisabled();
});

test('entity-val ger dirty state och konsekvens innan sparande', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === 'mobile', 'Samma formulärflöde täcks i desktopprojektet.');
  await page.goto('/settings');
  await page.getByRole('tab', { name: 'Entities' }).click();
  const outside = page.getByLabel('Välj utetemperatur');
  await outside.click();
  await page.getByRole('option', { name: /Vardagsrum.*sensor\.vardagsrum_temperature/i }).click();
  await expect(page.getByText('Osparade ändringar')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Konsekvens före sparande' })).toBeVisible();
  await expect(page.getByText(/aktiverar aldrig ett driftläge/)).toBeVisible();
});

test('HA-historikimport förklarar bevarande och visar resultat', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === 'mobile', 'Samma importflöde täcks i desktopprojektet.');
  await page.goto('/settings');
  await expect(page.getByRole('heading', { name: 'Historik för modellträning' })).toBeVisible();
  await expect(page.getByText(/befintliga snapshots skrivs aldrig över/i)).toBeVisible();
  await page.getByRole('button', { name: 'Importera' }).click();
  await expect(page.getByText(/8460 nya punkter importerades och 180 befintliga bevarades/i)).toBeVisible();
});

test('mobilvyn har ingen sidledes sidscroll och behåller statusnavigering', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile', 'Detta är en mobil kontroll.');
  await page.goto('/');
  await expect(page.getByRole('navigation', { name: 'Huvudnavigation' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Styrsystemets status' })).toBeVisible();
  const dimensions = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
  expect(dimensions.scroll).toBe(dimensions.client);
});
