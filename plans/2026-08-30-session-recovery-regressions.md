# Inloggning – felåterhämtning och proxyregressioner

## Omfattning och nuläge

Avgränsad uppföljning efter .NET 10-driftsättningen av `233afa4`. Ändringarna nedan är endast lokala och är inte publicerade eller driftsatta. Den befintliga `daikin`-stacken följs read-only; se [driftverifieringen](2026-08-30-production-verification.md).

Legacyalgoritm, ONECTA-payloads, jobbtider, kontoansvar, HA-credentials, databasmodell och driftlägen är oförändrade. `ControlMode=Legacy` / `DhwWriter=Legacy` och noll thermal-styrkommandon verifierades i produktionen 20:42 UTC. Ingen testsändning eller deploy gjordes i detta pass.

## Implementerat

- `SessionGate` har nu en tydlig svensk felvy med tangentbordsåtkomlig ”Försök igen”-knapp. Den gör endast en ny sessionskontroll och behåller aktuell URL.
- Anläggningsvyer visas inte när sessionskontrollen misslyckats, även om en tidigare autentiserad session ligger i cachen. Återförsök visar vänteläge och spärrar dubbla klick; ett nytt godkänt sessionssvar krävs innan innehåll visas igen.
- Råa HTML-/serverfel från proxy eller identitetsleverantör visas inte på den publika inloggningssidan. Texten förklarar att återförsöket inte ändrar inställningar eller scheman; den gör inget ogrundat påstående om pumpens driftstatus.
- Oförändrad CSRF-cookiekonfiguration har flyttats till `AccountAntiforgery`, som används både av `Program.cs` och de nya testen. Samma namn, HttpOnly, SameSite, produktions-/utvecklingspolicy och header gäller som före refaktoreringen.
- Elva nya integrationstester kör riktig ASP.NET Core-proxymiddleware och antiforgery med produktionsregistreringarna. De täcker betrodd IPv4 och IPv4-mappad IPv6, obetrodda/förfalskade headers, ett proxyhopp, oförändrad host, Secure/HttpOnly-cookie samt saknad/felaktig/kontofrämmande CSRF-token.
- Testnycklar och syntetiska identiteter stannar i minnet. Testerna startar inte `Program` med databas/migreringar/Hangfire/värmestyrning och bevisar inte en verklig användares fullständiga inloggning.

## Genomförda kontroller

- Release-bygge med installerad SDK 10.0.111 via explicit MSBuild: godkänt, inga byggvarningar eller fel. Repo/CI/Docker behåller låsningen till 10.0.400; den lokala SDK:n har inte ersatt den låsningen.
- Fokuserat `ProxiedAntiforgeryTests|TrustedProxyForwardingTests`: 14 godkända (11 nya + 3 befintliga), 0 fel.
- Full backend: 532 godkända, 9 befintligt överhoppade, 0 fel. Skipparna har inte dolts eller ändrats.
- Fokuserad `SessionGate`: 6 godkända; full Vitest: 14 godkända, 0 fel. Inkluderar tangentbord, vänteläge, cachelagrad autentisering, felåterhämtning och automatiska axe-kontroller. JSDOM:s färgkontrastregel är fortsatt undantagen; detta är inte en full manuell skärmläsarrevision.
- `npm run test:e2e`: TypeScript/Vite godkända; Playwright 8 godkända, 6 befintliga projektberoende undantag, 0 fel. Det nya återhämtningsflödet körs på både desktop och mobil och verifierar att inga andra API-/auth-anrop görs under fel/återförsök.
- Testernas bifogade desktop-/mobilbilder granskades visuellt: läsbara texter, tydlig åtgärd, ingen överlappning eller horisontell sidscroll. Bilderna är genererade lokala testartefakter, inte produktionsevidens.
- `git diff --check`: godkänd.

## Nästa steg

- Ingen omedelbar omdeploy behövs för dessa lokala ändringar. Före eventuell publicering/merge behövs CI för den nya revisionen; de sju gröna checkarna för produktionsrevision `233afa4` ska inte räknas som CI för denna kod.
- Fortsätt ordinarie read-only-uppföljning av legacykörningen 01:35 Europe/Stockholm. Den tidigare accepterade engångsskrivningen är dokumenterad, men en fysisk DHW-cykel är fortfarande inte verifierad här.
- Fullt autentiserat kontoflöde och kontobunden HA-konfiguration kräver verifierat kontoansvar. Äldre auto-apply-record utan token lämnas orört tills detta är utrett.
- Uppföljning 2026-08-31: den isolerade HTTP-testvärden för API-session/behörighet/CSRF är nu implementerad utan produktionsmigreringar eller värmearbetare. Tre tomma admin-HTTP-undantag har ersatts; sex andra backendundantag återstår. Se [den nya lokala verifieringen](2026-08-31-account-http-verification.md) för hittade fel, rättningar, UI-arbete och testresultat. Detta är fortfarande inte driftgodkännande.
- Verkliga Shadow-/komfort-/modell-/DHW-grindar återstår enligt huvudplanen. Ingen aktiv LWT eller gemensam DHW-writer har aktiverats.

Huvudplanen och driftverifieringen i kodrepositoryt är uppdaterade. De gemensamma dokumenten under Dokument är fortfarande blockerade av Kontrollerad mappåtkomst; den tidigare färdiga patchen ligger kvar. Skyddet har inte ändrats eller kringgåtts.
