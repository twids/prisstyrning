# Konto-API och utloggning – lokal verifiering 2026-08-31

## Resultat och avgränsning

En isolerad HTTP-testvärd kör nu samma sessions-, behörighets-, CSRF- och adminregistreringar som produktionen. Tre tidigare tomma, överhoppade admin-HTTP-tester är ersatta med verkliga tester. Arbetet hittade och rättade fel i API-svar, adminlistning, utloggningsåterkoppling och navigation.

Ändringarna är lokala på `codex/session-recovery-regressions`, ovanpå uppföljningen `14a57c8`. De är inte publicerade, CI-verifierade eller driftsatta. Produktionens revision är fortsatt `233afa4`; se [driftverifieringen](2026-08-30-production-verification.md). Inga produktionskonton, HA-credentials, scheman eller driftinställningar ändrades och ingen Daikin-skrivning utlöstes.

## Backend

- `AccountApiSecurity` delar produktionsregistreringarna för signerad cookie, databassession, API-gräns, CSRF, admininloggningens befintliga frekvensgräns och sessionsendpoints med testen. `AdminEndpoints` innehåller de befintliga adminhandlerna. `Program.cs` använder dessa; uppstartsmigreringar, Hangfire, legacyalgoritm, ONECTA-payloads och jobbtider är oförändrade.
- Ett anonymt `POST /api/session/logout` gav tidigare 302 till en inloggningssida. Cookiehändelserna ger nu uttryckligen 401/403 JSON för konto-API:er, oberoende av endpointens metadata. Detta var ett felaktigt redirectsvar, inte belägg för anonym åtkomst till anläggningsdata.
- Adminlistan visade tidigare bara tokenägare trots avsikten att också visa konton med inställningar eller historik. En read-only-union av konto-, token-, inställnings- och historik-ID:n visar nu även credentiallösa record. Testen verifierar att inga credentials returneras och att inga konton skapas, slås ihop eller ändras när listan läses. Befintliga auto-apply-inställningar lämnas orörda.
- 31 nya HTTP-testfall täcker signerade/förfalskade/återkallade/utgångna sessioner, avstängda eller saknade konton, kontofrämmande sessionsdata och CSRF-token, samtliga fyra mutationsmetoder, logout/återspelning av gammal cookie, adminbehörighet och fem inloggningsförsök per minut. De tre aktiverade admin-HTTP-testerna täcker anonym åtkomst, fullständig listning utan credentials och saknat adminlösenord.
- Testvärden använder `Microsoft.AspNetCore.TestHost` 10.0.11, isolerad EF InMemory-databas, temporära testfiler och kortlivade nycklar. Den läser inte driftkonfiguration och startar inte `Program`, migrationer, Hangfire, HA, EMHASS eller Daikin-klienter. Den syntetiska inloggningen som utfärdar testcookien finns endast i testprojektet.

TestServer är avsedd för isolerad HTTP-/middlewareverifiering utan lyssnande port eller certifikat. Testerna bevisar inte PostgreSQL-körning eller ett verkligt Daikin OAuth-utbyte; de publika OAuth-sentinelerna kontrollerar endast åtkomstgränsen. [Microsofts TestServer-dokumentation](https://learn.microsoft.com/en-us/aspnet/core/test/middleware?view=aspnetcore-10.0)

.NET 10:s automatiska hantering av cookieautentisering gäller kända API-endpoints. Därför testas även vårt eget uttryckliga kontrakt för 401/403 när sådan metadata inte har härletts, exempelvis för logout med tomt svar. [Microsofts dokumentation om API-autentisering](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/10/cookie-authentication-api-endpoints?view=aspnetcore-10.0)

## UX/UI

- Misslyckad utloggning ger ett synligt svenskt larm: utloggningen kunde inte bekräftas och användaren kan fortfarande vara inloggad. Råa serverfel visas inte. Ett nytt försök är möjligt och dubbla klick spärras under väntan.
- Desktop- och mobil-E2E provar ett misslyckat försök följt av lyckad utloggning. De verifierar CSRF på båda anropen, att inloggningsgränsen återkommer, att tidigare anläggningsdata inte visas efter logout eller webbläsarens Bakåt och att inga värmestyrningsanrop görs.
- Navigationens liststruktur är rättad till `ul > li`, och huvudnavigationens två responsiva varianter har olika tillgängliga namn. Inga axe-regler har stängts av för att dölja de funna felen; det tidigare JSDOM-undantaget för färgkontrast kvarstår.
- Visuell granskning hittade också hoptryckta, överlappande länkar i mobilnavigationen. Länkarna krymper inte längre; navraden går att scrolla. E2E kontrollerar att länktexterna får plats, att tangentbordsfokus kan nå sista länken och att själva sidan inte får horisontell scroll.
- Slutliga desktop-/mobilbilder för utloggningsfelet granskades visuellt. De är lokala testartefakter, inte produktionsbilder eller en fullständig skärmläsarrevision.

## Verifiering

Första körningarna fångade kompileringsfel i refaktoreringen samt de två HTTP-felen ovan. Utloggningstestet fångade den saknade felåterkopplingen. Därefter fångade axe/navigationstest och visuell granskning HTML-/mobilfelen; de rättades före slutkontrollerna.

- Release-bygge: godkänt, inga byggfel eller varningar. Lokalt används installerad SDK 10.0.111 explicit; repo, CI och Docker behåller SDK-låsningen 10.0.400.
- Fokuserat `AccountApiSecurityTests|AdminEndpointTests|ProxiedAntiforgeryTests`: **63 godkända**, 0 fel/undantag.
- Full backend: **566 godkända, 6 överhoppade, 0 fel** (572 totalt). Sex befintliga undantag återstår: två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. Endast de tre tomma admin-HTTP-undantagen har ersatts i detta arbete.
- Full Vitest: **17 godkända**, 7 testfiler, 0 fel. Inkluderar tre nya Layout-tester för felåterkoppling, återförsök, vänteläge och axe.
- `npm run test:e2e`: TypeScript/Vite godkända; **10 tillämpliga Playwright-flöden godkända**, 6 avsiktliga undantag för dubblerade desktop-/mobilprojekt, 0 fel.
- `git diff --check`: godkänd. `Jobs`, `BatchRunner.cs`, `ScheduleAlgorithm.cs`, migrationer och båda compose-exemplen är oförändrade i detta pass.

Backendkontrollerna kördes från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontendkontrollerna kördes med `npm test` och `npm run test:e2e` i `frontend`.

## Produktion och nästa steg

- Read-only-kontrollen 2026-08-30 21:41 UTC / 23:41 CEST visade oförändrad app-/EMHASS-image, tre körande containrar med noll omstarter, friska hälsosvar, anonym session och 401 på skyddad thermal-status. Databasen hade fortsatt `Legacy/Legacy` och noll thermal-styrkommandon.
- Ingen senare legacykörning hade tillkommit i de kontrollerade loggarna. Nästa ordinarie körning är 2026-08-31 01:35 CEST / 2026-08-30 23:35 UTC; den var ännu inte observerad i detta pass. Den tidigare accepterade engångsskrivningen är dokumenterad separat och bevisar inte en fysisk DHW-cykel.
- Publicering/merge kräver CI för dessa nya revisioner. Den driftsatta PR #120:s gröna checkar är inte verifiering av de lokala ändringarna. Ingen automatisk omdeploy görs för denna uppföljning.
- Användarens autentiserade kontoflöde, kontobundna HA-anslutning och verkliga Shadow-/komfort-/modell-/DHW-grindar återstår. Äldre auto-apply-record utan token lämnas orört tills kontoansvar och mandat har verifierats.
- Uppföljning 2026-08-31: rumsvyns historiska larmnivåer och presentation av faktisk givarkvalitet/reservvärden är rättade lokalt. Se [rumskvalitetsrapporten](2026-08-31-room-quality-verification.md) för verifiering och en separat kvarvarande brist i statusradens samlade kvalitetsbedömning.
- Den befintliga adminraderingens livscykel är inte verifierad eller ändrad. Den använder fortfarande en generell historikrensning med `DateTimeOffset.MinValue`, som inte raderar vanlig kontohistorik, och tar inte bort alla konto-/sessionsdata. Behandla kontoradering separat med uttrycklig datamodell, tester och korrekt kontoansvar innan den betraktas som fullständig.

Huvudplanen, den tidigare lokala uppföljningen och driftverifieringen i kodrepositoryt är uppdaterade. Delade `README.md`/`INFRASTRUCTURE.md` under Dokument är fortfarande blockerade av Kontrollerad mappåtkomst; den färdiga patchen ligger kvar oförändrad. Skyddet har inte ändrats eller kringgåtts.
