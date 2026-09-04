# HA-katalog och sensorval – verifiering 2026-08-31

## Resultat och avgränsning

Fortsättning från `418d892` på `codex/session-recovery-regressions`. Katalogen och sensorväljarna är rättade och verifierade **lokalt**, inte publicerade eller driftsatta. Den befintliga produktionsstacken följs enbart read-only. Legacyalgoritm, ONECTA-payload, jobb, aktiveringsgrindar och styrklienter är oförändrade.

Tidigare gav `/api/home-assistant/entities` `Valid` åt alla nyligen uppdaterade strängar, även `unknown` och `unavailable`. Mottagningstid, brutna anslutningar, framtida tidsstämplar och enhetens format saknade kontroll. En ändrad katalog eller ett läsfel kunde också få en sparad mappning att se tom ut, medan cachedata fortfarande visades som godkända. Rumsväljaren dolde temperaturgivare i exempelvis Fahrenheit trots att normaliseraren stöder omräkning.

## Implementerat

- En separat, ren katalogbedömning hanterar otillgängliga värden, NaN/Infinity/överflöde, felaktigt enhetsformat, saknade/motsägelsefulla/framtida tider och kontots egen åldersgräns. Tidskontrollen tillåter högst 30 sekunders klockavvikelse. Ett felaktigt namn-attribut faller tillbaka till entity-ID utan att sänka hela svaret.
- Kontot hämtas endast från den verifierade sessionen. Inaktiv/raderad anslutning eller saknad telemetritoken ger en tom katalog även om cache finns kvar. Bruten liveanslutning eller startbild äldre än anslutningsinställningarna ger inte ett godkänt resultat.
- Additiva DTO-fält `compatibleUnits`, `checkedAtUtc` och `validUntilUtc` beskriver en preliminär värde-/enhetskontroll. Befintliga fält och numeriska `DataQuality`-värden behålls. Enhetsomräkning återanvänder den befintliga normaliseraren; booleska signaler förväxlas inte med en temperatur på 0/1. Prognoskompatibilitet kräver läsbara enheter och minst två kommande temperaturpunkter.
- `Quality=Valid` i katalogen avser tillgänglighet/färskhet, inte automatiskt en viss roll. Väljaren kräver dessutom rätt enhetskompatibilitet innan den visar **Värde/enhet OK**. Rimliga intervall, förändringshastighet, historik, exkludering, komfort och readiness bedöms separat. Ingen läsning anropar eller ändrar `SensorQualityTracker`.
- Gemensam entity- och rumsväljare visar namn, exakt ID, råvärde/enhet, HA:s uppdateringstid, lokal mottagningstid och kontrollresultat även efter valet. Ett saknat ID bevaras uttryckligen; omladdning eller fel raderar aldrig mappningen. Fel enhet får inline-förklaring. Detta är återkoppling för konfiguration, inte ett nytt tillstånd att aktivera styrning.
- Fel vid katalog-/status-/konfigurationshämtning döljer gamla godkännanden och värden samt låser själva väljaren till återhämtning. **Uppdatera sensorlistan** använder enbart befintliga GET-endpoints, inte anslutningstest, HA-serviceanrop eller ändrade inställningar.
- Gränssnittet åldrar kontrollen var 30:e sekund: kontots giltighetstid gäller, och en katalogbedömning äldre än två minuter visas inte som aktuell även om polling pausat. Äldre API-svar utan de nya kontrollfälten blir okontrollerade, inte gröna.
- Svensk, kortare inställningstext, bättre rumsformulär på smal skärm, namngivna fältgrupper och en namngiven popup-region. Tangentbord, återhämtning och 320-pixelslayout är testade.

## Kodverifiering

| Kontroll | Resultat |
|---|---|
| .NET 10 Release-bygg, med restore och `ContinuousIntegrationBuild=true` | Godkänt; inga rapporterade varningar/fel |
| Riktad backendkörning `FullyQualifiedName~HomeAssistantEntityCatalog` | 64/64 vid första körningen; ytterligare ett normaliserar-paritetsfall ingår i helsviten |
| Hela backendens Release-svit | **753 godkända, 6 befintliga undantag, 0 fel**, totalt 759 |
| `npm.cmd test -- entityCatalog HomeAssistantEntityPicker ThermalSettingsPage` | **31/31** |
| `npm.cmd test` | **138/138**, 12 testfiler |
| `npm.cmd run build` / bygget i `test:e2e` | TypeScript och Vite godkända |
| `npm.cmd run test:e2e -- entity-catalog.spec.ts` | **2/2**, desktop och mobil |
| `npm.cmd run test:e2e` | **18 godkända, 6 avsiktliga projektexkluderingar, 0 fel**, totalt 24 |
| `git diff --check` | Godkänt; endast Git-notiser om normal LF/CRLF-konvertering |

Lokala .NET-kommandon, från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /p:VSTestTestCaseFilter=FullyQualifiedName~HomeAssistantEntityCatalog /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Repository/CI/Docker är fortfarande låsta till SDK 10.0.400; lokalt finns 10.0.111. Ingen pin ändrades. Frontendkommandona ovan kördes från `frontend`.

65 nya backendfall täcker den rena projektorn och den verkliga HTTP-routen med testsessioner och isolerad lagring. Endpointvalidatorn kastar om testvärden skulle försöka öppna en extern anslutning. Inga HA-/EMHASS-/ONECTA-klienter eller kontrollworkers registreras. Upprepade GET bevarar Legacy/Legacy och skapar inga styrkommandon, tillstånd, events eller telemetrirader. Tester täcker bland annat kontoisolering, felaktiga attribut, inaktiverad/borttagen anslutning, gammal startbild och återhämtning.

Det nya browserflödet provar fel enhet, Fahrenheit-givare, otillgängligt värde, läsfel, återhämtning, sparad saknad rumsgivare och dirty-state. Det bekräftar noll skrivande API-anrop. Sex kompletta fältbilder samt sex inledande sidbilder granskades för desktop/mobil. Bilderna finns som ignorerade Playwright-artefakter; de innehåller enbart syntetiska testdata, inte produktionsacceptans.

Tre tidiga testutfall åtgärdades innan helsviterna godkändes:

- Axe upptäckte att popupen saknade ett namngivet område. Popupen fick en namngiven region; regeln stängdes inte av.
- Det nya klienttestet förutsatte uttrycklig `method: GET`, men den befintliga klienten använder Fetchs GET-standard. Testet rättades utan produktändring.
- Ett äldre browsertest matchade både den nya fältgruppen och komboboxen. Det använder nu exakt kombobox-roll/namn i stället för en tvetydig etikettmatchning.

De sex backendundantagen är fortsatt två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. EF InMemory/TestServer ersätter inte PostgreSQL-, full startup-, riktig OAuth- eller nätverksacceptans. JSDOMs axe-körning undantar färgkontrast; bilderna är visuellt kontrollerade men full skärmläsar-/kontrastverifiering återstår. Inga nya CI-, publish- eller containerbyggen gjordes för denna källkodsserie.

## Produktionskontroll, separat evidens

Read-only verifierat omkring **2026-08-31 06:00 CEST / 04:00 UTC**:

- App, PostgreSQL och EMHASS körde med noll omstarter. App- och EMHASS-referenser matchade den tidigare verifierade driftsättningen i [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session`: HTTP 200. Sessionen var oautentiserad; endast boolesk information om utfärdad CSRF visades.
- Anonym `/api/thermal/status` **och** `/api/home-assistant/entities`: HTTP 401.
- En uttrycklig `BEGIN TRANSACTION READ ONLY` bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster från 02:53:04 UTC till 04:00:00 UTC. Tidigare accepterad ordinarie skrivning 01:35 CEST är redan dokumenterad; ingen ny körning utlöstes.
- Inga kontoinställningar, credentials, behörigheter, scheman, containrar eller aktiveringsspärrar ändrades. Ingen token, tokenfragment, tokenlängd eller rålogg skrevs ut.

## Återstår / nästa avgränsning

1. **Kontobundet byte av HA-anslutning – uppföljt i lokal kod:** revisionsstyrd omladdning, isolerade avbrotts-/race-tester och tydlig live-status är nu implementerade enligt [den efterföljande rapporten](2026-08-31-ha-reload-verification.md). Ändringen är inte driftsatt. GET-knappen hämtar fortsatt bara status; omladdning sköts automatiskt efter sparning. Inga styrkommandon eller verkliga credentials ändrades.
2. **Insamlingskedjans motsvarande gränser:** denna projektor ersätter inte `SensorValueNormalizer`/`SensorQualityTracker`. Fortsätt separata regressioner för icke-ändliga tal, malformed attribut samt saknade/framtida råtider genom faktisk insamling, historik och readiness innan aktiv styrning övervägs.
3. Granska den samlade lokala ändringsserien och kör tillämplig CI före en motiverad uppdatering av samma Dockhand-stack. Den lokala adminraderingsspärren är fortfarande inte driftsatt; använd inte den gamla produktionsraderingen.
4. Verklig kontoinloggning, kontoägd HA-konfiguration och husets Shadow-/modell-/DHW-acceptans återstår. Äldre tokenlösa produktionskonton får inte ändras utan verifierat kontoansvar. Ett accepterat ONECTA-anrop verifierar inte en fysisk varmvatten- eller hygiencykel.

Meningsfullt godkänt arbete återstår; den befintliga timvisa fortsättningen har inte ändrats eller stängts. Huvudplanen och produktionsrapporten är uppdaterade. Gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument är fortsatt oförändrade på grund av Kontrollerad mappåtkomst; tidigare förberedd patch finns kvar och skyddet har inte kringgåtts.
