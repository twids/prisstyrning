# Samlad datakvalitet och verkligt API-format – 2026-08-31

## Resultat och avgränsning

Statusradens falska `Valid` är rättad **lokalt**, tillsammans med ett upptäckt enumkontraktsfel mellan API och frontend. En färsk snapshot räcker inte längre för grön datakvalitet. Produktionens image, jobb och styrning är oförändrade: `ControlMode=Legacy`, `DhwWriter=Legacy`, inga nya thermal-kommandon.

Arbetet fortsätter från `464642c` på `codex/session-recovery-regressions`. Ingen push, merge, driftsättning, testsändning till Daikin, kontoändring eller aktivering ingår i detta pass. Tidigare lokala sessions-/konto-/rumsvyrättningar är också fortfarande separata från den driftsatta revisionen `233afa4`.

## Backend och kontrakt

- `ThermalStatusQuality` bedömer senaste sparade snapshot mot just det inloggade kontots aktiverade rum och entity-mappningar. Inaktiverade eller omappade datakällor drar inte ned kvaliteten; om ingen är aktiverad är status `Unavailable`.
- Giltighet kräver en tolkbar kvalitetsflagga och explicit `Excluded=false` för givare. En givare under återhämtning förblir ogiltig för sammanfattningen även om senaste enskilda mätningen är giltig. Kritiska rummets positiva reservvärde kan inte göra givaren giltig. Befintlig tre-fel/tre-giltiga-logik ändras inte.
- Giltiga rumsvärden måste dessutom finnas, vara ändliga och ligga inom rummets tillåtna mätintervall. Ett kallt men korrekt uppmätt rum är fortfarande datamässigt giltigt; komfortskyddet bedöms separat.
- Mappade numeriska och booleska roller kontrolleras mot snapshotens värden. Noll, negativa spotpriser och booleskt `false` är inte saknade värden. En mappad prognos läses ur sin separata kvalitetsdel, utan att kräva den exkluderingsflagga som prognoser inte sparar. Omappad prognos ignoreras.
- `heating_deviation` har ingen egen numerisk telemetrikolumn. Dess bedömning använder insamlarens kvalitets-/exkluderingsmetadata, inte det senaste beordrade LWT-värdet som ersättning för mätning.
- Saknad/okänd kvalitet eller källa, importerad historik, framtida tidsstämpel och mer än tio minuter gammal insamling kan inte bli `Valid`. Efter konfigurationsändring inväntas en ny snapshot; dess femminutersbucket måste vara minst lika ny som konfigurationens uppdateringstid. Detta kan ge en kort väntan även när en insamling gjordes senare inom samma bucket.
- För en färsk, identifierbar liveinsamling används explicit prioritet `Invalid` → `Unavailable` → `Stale` → `Valid`. Ett ogiltigt/exkluderat aktivt underlag döljs alltså inte av att andra underlag är giltiga. Förklaringen redovisar antal per klass utan att återge råa sensorvärden eller sparade feltexter.
- API:t får det additiva fältet `dataQualityReason`. Befintliga fält, numeriska enumvärden, tabeller, ONECTA-payloads och legacyjobb behåller sitt format. Statusanropet är läsande och skapar inte ens en installation för ett nytt konto.
- Statusrutten och dess befintliga kontofilter kan monteras separat i den isolerade HTTP-testvärden. Produktion använder samma route och filter; inga HA-/EMHASS-/Daikin-klienter eller arbetare behövs i dessa tester.

Det riktiga HTTP-testet visade dessutom att ASP.NET skickar `mode`, `dhwWriter` och `overallDataQuality` som **siffror**. Tidigare frontendantaganden och fixtures använde namn utan någon översättning. Klienten översätter nu kända numeriska eller namngivna svar för status, readiness och entity-lista. Okända enumvärden avvisas, aldrig som ett gissat `Legacy` eller `Valid`. Avsiktliga lägesbyten skickas som numeriskt JSON enligt den redan befintliga serverbindningen, fortsatt med CSRF och samma server-side readiness-/aktiveringsspärrar. DTO/JSON-tester verifierar formatet utan att anropa någon lägestjänst.

## UX/UI

- Statusraden namnger datakvaliteten, visar ikon/text/färg och en läsbar förklaring med länkar till rum och givarmappning. Den säger uttryckligen att komfort och tillåtelse till aktiv styrning bedöms separat.
- Tidsetiketten avser **senast sparade insamling**, inte en påstått giltig mätning. LWT-värdet använder svensk decimalformatering.
- Under första hämtningen visas inte påhittat driftläge eller tillgänglighet. Vid hämtningsfel döljs cachad grön status och aktuella värden. Om senast kända driftläge var aktivt finns rollbackknappen kvar; den utför ingenting utan ordinarie användarbekräftelse. Läges-/rollbackknapparna ligger utanför den horisontellt rullbara raden så att säkerhetsvägen även syns direkt i aktivt läge vid 320 px.
- En lokal 30-sekundersklocka åldrar en tidigare giltig status även när nätpollning har stannat. Saknad/ogiltig/framtida tid kan inte ge en grön badge; felaktig tid kraschar inte relativ tidsformatering.
- Rumspresentationen behandlar även annan skiftlägesform av importkällan och okända uttryckliga källor som icke-aktuella. Sammanfattning och rumskort provas tillsammans för fel och återhämtning, samtidigt som ett giltigt kallt rum behåller komfortvarningen.
- Den lokala visuella testservern använder nu API:ts verkliga numeriska enumformat. Alla browserdata är fortfarande syntetiska och servern kontaktar inga verkliga integrationer.

## Kontroller

Det första isolerade HTTP-regressionsprovet misslyckades som väntat: en exkluderad kritisk givare gav `0` (`Valid`) där `2` (`Invalid`) krävdes. Efter rättning passerar det. Under utökningen rättades två rena testproblem: xUnit 2 behöver `21d` för nullable double i InlineData, och RTL:s `getByRole` har inte Playwrights `exact`-option.

- Release build: **godkänd**, inga byggvarningar eller fel. Installerad SDK **10.0.111** användes uttryckligen; repo/CI/Docker är oförändrat låsta till **10.0.400**.
- Full nybyggd backendregression: **671 godkända, 6 befintligt överhoppade, 0 fel** (677 totalt). Tillskottet är 90 bedömningsfall, 11 riktiga HTTP-statusfall och 4 DTO/JSON-fall. HTTP-fallen täcker autentisering, frånvaro av styrklienter, kontoisolering även med manipulerad queryparameter, exkluderad givare, ändrad konfiguration, återhämtning och läsning utan thermal-skrivningar.
- De sex backendundantagen är fortfarande två BatchRunner-persistensfall, tre ScheduleHistory-integrationsfall och ett live-Nordpoolfall. De har inte räknats som passerade.
- `npm test`: **98 godkända, 0 fel**, nio testfiler. Detta är 38 fler än föregående rumspass. API-format, felaktiga enumvärden, CSRF-bevarande, statusfel/cache/ålder/återhämtning, importerad källa och tillgänglighet ingår.
- `npm run test:e2e`: **14 tillämpliga desktop-/mobilflöden godkända**, sex befintliga undantag för duplicerade projekt och 0 fel. TypeScript/Vite-build passerar. De nya/samordnade kvalitetsflödena kontrollerar frånvaro av alla API-mutationer, numeriska svar, exkludering/återhämtning, hämtningsfel, okända enumvärden, gammal cache och layout ned till 320 px.
- Diffkontrollen bekräftade noll ändringar i `Program.cs`, legacyalgoritm/-jobb, regulatorer, modeller/optimerare, databasmodell/-migrationer, Compose eller SDK-/projektversioner. Endast status-API, dess additiva DTO, läsande bedömning, klient-/UI-kod, tester och planer ingår.
- `git diff --cached --check`: godkänd, även för tillagda filer. Efter sista godkända test-/bildkörningen ändrades endast indrag och dokumentation.
- Desktop-/mobilbilder av status, rum och hämtningsfel granskades visuellt. Axe-kontrollerna i JSDOM behåller det tidigare undantaget för färgkontrast; detta är inte en full manuell kontrast- eller skärmläsarrevision.

Kommandon från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontendkommandona kördes i `frontend`. EF InMemory/TestServer, frontend-fixtures och lokal bildgranskning ersätter inte PostgreSQL-/full-startup-/autentiserad produktionsverifiering. CI har inte körts för den nya lokala serien i detta pass.

## Read-only-produktion

Uppföljningen gjordes omkring **03:49 CEST / 01:49 UTC** den 31 augusti:

- App, PostgreSQL och EMHASS var `running`, noll omstarter, och digests matchade [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session` gav 200. Sessionen var `authenticated=false` med utfärdad CSRF-token; värden eller längder för token/cookies skrevs inte ut. Skyddad `/api/thermal/status` gav 401.
- En uttrycklig `BEGIN TRANSACTION READ ONLY` bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster fanns i det kontrollerade loggfönstret från 00:43:15 UTC. Den tidigare verifierade ordinarie 01:35 CEST-körningen är fortsatt tidigare evidens, inte en ny testsändning eller ny fysisk DHW-verifiering.
- Inga credentials, äldre kontoinställningar, scheman, driftsättningar eller aktiveringsspärrar ändrades.

## Återstår

Samla och granska den lokala sessions-/konto-/rum-/statusserien för tillämplig CI innan eventuell motiverad uppgradering i samma Dockhand-stack. Nuvarande gröna produktions-PR gäller inte dessa nya revisioner. Varje sådan uppgradering ska behålla `Legacy/Legacy`, aktiveringsspärrarna och den dokumenterade reversibla rollbackvägen; utlös inte en ny Daikin-testsändning av rutin.

Nästa avgränsade kodkontroll är den tidigare dokumenterade adminraderingens livscykel: verifiera först i den isolerade HTTP-testvärden exakt vilka kontobundna poster som berörs och att andra konton bevaras. Inga verkliga konton får raderas eller ändras av denna uppföljning. De sex backendundantagen och full programstart/integrationsverifiering är också öppna.

En separat presentationsgräns observerades i `/api/home-assistant/entities`: katalogens preliminära kvalitetsflagga beräknas fortfarande från tidsålder, inte från värde/enhet som i femminutersinsamlaren. Klientens enumöversättning rättar formatet, inte denna bedömning. Prova katalogvisning av färsk `unknown`/`unavailable` och rollspecifik validering i en senare avgränsad HA-/entity-UX-uppföljning; likställ inte katalogflaggan med full sensorvalidering.

Verkligt autentiserat kontoflöde, kontobunden HA-konfiguration, husets Shadow-/modell-/komfortverifiering samt DHW- och hygiencykler återstår före aktiv intelligent styrning. Korrekt datakvalitet i UI är inte ett uppfyllt aktiveringskrav i sig.

Huvudplanen, föregående rumskvalitetsrapport och produktionsrapporten är uppdaterade i kodrepositoryt. De gemensamma dokumenten under Dokument lämnas oförändrade på grund av Kontrollerad mappåtkomst; den befintliga färdiga patchen och skyddet ändras inte. Automationen behålls eftersom meningsfullt godkänt verifieringsarbete återstår.
