# Väder, rapportintervall och temperaturgraf

## Avgränsning

Användaren har godkänt implementation och driftsättning i befintlig Dockhand-stack. Legacy/Legacy och spärrarna för LWT/FullActive ska bevaras. Inga kontoinställningar ändras automatiskt och ingen testsändning behövs för releasen.

## Implementation

- Val av weather.* och kontoautentiserat prognostest via den fasta, läsande HA-åtgärden weather.get_forecasts med hourly och return_response. Ingen generell servicestyrning exponeras. Separat hämtning av enheter för Celsius- och vindkonvertering; svar från andra entities används inte. Felinnehåll och token loggas inte. Samma hämtning används av insamlaren.
- Test visar verkligt tidsomfång, punktantal och vind-/soltäckning, utan löfte om 48 timmar. Standardiserad weather-data ger inte automatiskt solinstrålning. Befintliga template-mappningar bevaras i backend.
- Temperaturhistorik på Plan visas även utan optimeringsplan: LWT, retur, ute och medel av giltiga rumsgivare. Ersatta rumsvärden visas inte som uppmätta. Avläst och föreslagen LWT-avvikelse separeras från absolut temperatur. Tidszon Stockholm och null-luckor vid insamlingsavbrott.
- Nullable rapportintervall per rum/entity via additiv EF-migration. Fysisk rapportålder och högst tio minuter sedan HA-avläsning bedöms separat. Uteblivna rapporter innebär osäkerhet, inte tre felaktiga mätningar. Orimliga värden ger fortfarande Invalid/exkludering och tre verkliga rapporter krävs för återhämtning.
- Befintlig konfigurationsfingerprint bevaras när nya rapportintervall är null. Faktiskt ändrade intervall påverkar modellreferensen.
- EMHASS visar avstängd/ej verifierad separat från verifierad tillgänglighet.

## Lokal verifiering

- dotnet build --configuration Release --no-restore: grönt, inga varningar/fel.
- dotnet test --configuration Release --verbosity quiet: 1233 godkända, 7 tidigare överhoppade.
- npm run build: grönt.
- npm test -- --run: 250 godkända i 21 filer.
- npm run test:e2e: 28 godkända, 6 avsiktligt överhoppade mobil/desktop-dubbletter.
- npx playwright test e2e/weather-temperature.spec.ts: ytterligare 4 godkända, vädertest utan konfigurationsskrivning och temperaturgraf utan plan på desktop/mobil.
- Mobilskärmbild granskad. En tom avvikelsegraf döljs när både plan och avläst avvikelse saknas.

## Återstående driftverifiering

CI, imageattestering och deployment är ännu inte verifierade för denna release. Före deployment: ny compose/databasbackup, digestlåst image, endast appens image ändras. Efteråt verifieras migration, hälsa, anonymt inloggningsskydd, Legacy/Legacy och oförändrade aktiveringsspärrar. Vädertestets faktiska utfall beror på kontots HA och stöd för hourly; syntetiska tester är inte driftbevis.

Rollback ska återställa föregående appdigest via Dockhand; de två nullable databaskolumnerna kan ligga kvar. Nätverk, PostgreSQL, EMHASS och kontosecrets ska inte ändras. Gemensamma dokument under Dokument är tidigare blockerade av Kontrollerad mappåtkomst; skyddet ska inte kringgås.
