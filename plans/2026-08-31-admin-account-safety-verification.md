# Kontoradering och admin-UX – lokal verifiering 2026-08-31

Status: avgränsad säkerhetsrättning i lokal källkod ovanpå `78be390`. Ingen publicering, CI-körning, merge eller driftsättning ingår. Produktionen kör fortfarande `233afa4` i befintlig Dockhand-stack med `Legacy/Legacy`. Raderingsspärren nedan finns därför **inte ännu i produktion**; använd inte den gamla adminraderingen där.

## Verifierad brist

Två tillfälliga karakteriseringstester anropade den oförändrade DELETE-handlern genom den isolerade HTTP-värden:

- För ett syntetiskt konto försvann Daikin-token samt admin-/Hangfire-behörighet från `admin.json`. Svaret var HTTP 200 med `deleted=true`.
- Kontot, giltig session, `AutoApplySchedule=true`, schemahistorik, HA-anslutning, installation, thermal-writer/lease och optimeringsjobb låg kvar. Kontots session var fortfarande autentiserad efteråt. Det andra testkontots token, inställningar, historik och behörigheter var bevarade.
- Även ett konto som inte fanns gav `deleted=true`. Den globala historikstädningen anropades med `DateTimeOffset.MinValue`, vilket inte raderade normala historikrader och inte var kontospecifik radering.

Karakteriseringarna blev båda gröna och ersattes sedan med regressioner för den säkra spärren. Tre äldre tester som bara manipulerade testkataloger eller jämförde ID-strängar ersattes; de anropade aldrig den verkliga raderingshandlern. Detta bevisar ett kodfel, **inte** att det äldre tokenlösa produktionsrecordet uppstod genom en radering. Inget produktionskonto har raderats eller ändrats för felsökningen.

## Rättning och avgränsning

`DELETE /api/admin/users/{userId}` behåller sin route, sessions-/adminskydd, antiforgery och validering av ID/eget konto. Efter dessa kontroller returnerar den alltid HTTP 409 med `code=account_deletion_unavailable`, `deleted=false` och en svensk förklaring. Den anropar inte längre någon raderingsrepository eller behörighetsmutation. Det finns ingen konfigurationsflagga som kan kringgå spärren.

Detta är avsiktligt en **spärr, inte en färdig kontoraderingsfunktion**. Ett säkert fullständigt flöde behöver samordna pågående och redan inlästa jobb, writer-lease, säker LWT-återgång, sessionsåterkallelse, HA/ONECTA-åtkomst och beslutad hantering av historik/revisionsdata. Att bara lägga till fler DELETE-anrop skulle inte göra den processen säker. Ingen ny raderings- eller gallringspolicy har antagits.

Legacys scheman, payloads, inställningar och jobb är oförändrade. Inga ändringar har gjorts i databasschema, migrationer, regulatorer, modeller, Docker Compose eller aktiveringsspärrar. Administratörens separata behörighetshantering behåller sina befintliga API:er. Normal sessionsvalidering kan fortfarande uppdatera administratörens egen aktivitetstid; spärren gör inga ändringar i målkontot.

## UX/UI

- Adminvyn förklarar spärren med synlig svensk information. Raderingsknappar är inaktiva, har fullständiga tillgängliga namn och hänvisar till förklaringen; det missvisande löftet om permanent radering och dess bekräftelsedialog är borttagna.
- Fel i adminbehörighetskontrollen döljer cachade konton/åtgärder och erbjuder ett läsande återförsök. Fel i användarlistan döljer också gammalt innehåll tills en ny hämtning lyckas.
- Behörighetsändringar är inaktiva under uppdatering av status/lista. Felmeddelanden förklarar att resultatet inte kunde bekräftas och återger inga råa serverfel. Adminlösenord/credentials skrivs inte ut.
- Listan har ett namngivet, tangentbordsfokuserbart rullningsområde; behörighetsreglagen har uttrycklig switch-roll och kontospecifik etikett. Daikin-ikoner beskriver förekomst av auktorisering, inte verifierad nätanslutning.
- Screenshots av normal vy, listfel och behörighetsfel granskades på desktop och 320 px mobil. Uppgifter kan rullas inom tabellen utan sidledes dokument-scroll. Den gemensamma statusraden/rollbacken finns kvar. Dessa screenshots använder syntetiska data och är inte produktionsbevis.

## Kodverifiering

- Release-build med installerad SDK 10.0.111: godkänd utan varningar eller fel. Repo/CI/Docker behåller låsningen till 10.0.400; ingen SDK-konfiguration ändrades.
- Hela backend-sviten: **688 godkända, 6 befintliga undantag, 0 fel** (694 totalt). Förändring från föregående 671/6: 20 verkliga HTTP-fall tillkom och 3 missvisande äldre tester ersattes.
- De 20 HTTP-fallen omfattar alla fyra driftlägen med syntetiska aktiva leases/jobb, upprepade anrop, saknat konto, äldre ensamma inställnings-/historik-/tokenrader, adminlösenord, session/admin/CSRF-skydd, självradering och ID-gränser. Alla 23 mappade tabellers skalärvärden jämförs före/efter i testet med två kompletta konton, inklusive ägarskap, krypterade testvärden, leases, planer/steg, verifierad DHW och revisionslogg. Båda kontonas behörigheter bevaras.
- Full Vitest: **108 godkända tester i 10 filer**, inklusive 9 admin-/tillgänglighetstester och ett klientkontrakt för HTTP 409. Klienten behandlar spärren som ett fel, aldrig som lyckad radering.
- TypeScript/Vite-build: godkänd. Full Playwright: **16 tillämpliga desktop-/mobilflöden godkända**, 6 befintliga projektspecifika dubbleringsundantag, 0 fel (22 totalt). De nya adminflödena verifierar även att visning, fel och återförsök inte skickar några API-mutationer.
- Automatiska axe-kontroller är godkända. JSDOM undantar fortfarande färgkontrast; detta ersätter inte full manuell kontrast-/skärmläsargranskning.
- `git diff --check`: godkänd. Ändringarna är begränsade till adminhandler/vy, regressionstester och verifieringsdokumentation.

Första fokuserade körningen upptäckte fel testförväntan för CSRF (befintligt HTTP 400, inte 403). Endast testförväntan rättades. UI-testerna hittade även att bibliotekets input-slot ersatte standardrollen och att tooltip-etiketten tog över ikonens namn; detta rättades med uttrycklig roll respektive beskrivande tooltip. De slutliga fullständiga körningarna ovan omfattar rättningarna.

Körkommandon från repo respektive `frontend`:

```text
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll" Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll" Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
npm.cmd test
npm.cmd run test:e2e
```

HTTP-värden använder EF InMemory, testidentiteter och tillfälliga nycklar. Den startar varken Program, migrationer, Hangfire, HA, EMHASS eller ONECTA. Testerna bevisar därför inte PostgreSQL-transaktioner, full applikationsuppstart, verklig Daikin-inloggning eller en framtida fullständig kontoradering. Sex äldre backend-undantag återstår: två BatchRunner-persistenstester, tre schemahistoriktester och ett nätberoende Nordpool-test.

## Read-only driftkontroll

2026-08-31 omkring **04:53 CEST / 02:53 UTC**:

- Befintliga app-, PostgreSQL- och EMHASS-containrar körde med noll omstarter och samma image-referenser som i [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session` svarade 200; sessionen var oautentiserad och CSRF utfärdades. Anonym `/api/thermal/status` gav 401. Endast statuskoder och boolesk sessionsinformation skrevs ut.
- En uttrycklig read-only databastransaktion bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster observerades mellan 01:49:10 UTC och kontrollen 02:53:04 UTC. Den tidigare verifierade ordinarie 01:35 CEST-körningen kvarstår som evidens; inget jobb eller extra testsändning startades.

## Nästa steg och dokumentation

Granska den samlade lokala ändringsserien och kör tillämplig CI före en motiverad uppdatering av samma Dockhand-stack. Gör ingen deploy bara för att automationen startar. Fullständig kontolivscykel/radering förblir ett separat öppet arbete; ingen produktionsradering eller implicit ändring av det äldre auto-apply-kontot är godkänd.

Nästa avgränsade källkodsuppgift är entity-väljarens preliminära kvalitet: färsk `unknown`/`unavailable` får inte visas som giltig, och roll/enhet måste skiljas från insamlarens validerade telemetri. Kontobunden HA-konfiguration, verkliga Shadow-/värmekurvedygn och DHW-verifiering återstår före aktiv styrning.

Denna rapport, huvudplanen och produktionsrapporten uppdateras i kodrepositoryt. Gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument är fortsatt oförändrade på grund av Kontrollerad mappåtkomst. Den befintliga [förberedda patchen](2026-08-30-shared-infrastructure-update.patch) är bevarad; skyddet har inte ändrats eller kringgåtts.
