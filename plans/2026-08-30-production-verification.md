# Prisstyrning – driftsättningsverifiering 2026-08-30

## Resultat

Den befintliga Dockhand-stacken `daikin` är uppgraderad till .NET 10-revision `233afa4c7d8a2784ce7e80179e0b7df8534f34da` (PR #120). Ingen parallell stack skapades. Legacy är ensam DHW-writer; LWT/FullActive är inte aktiverade.

Appimage: `ghcr.io/twids/prisstyrning@sha256:9e0fe803de6cda3e7154459eb0afc0a3743f3455b0a4c6aaa24991bd39e9e22d`.

Verklig ONECTA-skrivning lyckades kl. 19:52:54 UTC / 21:52:54 CEST. Loggen innehöll både `apply OK` från legacy-skrivvägen och `Applied` från `ScheduleUpdateHangfireJob`. Detta bevisar accepterad API-skrivning, inte en fysisk uppvärmningscykel.

Även ordinarie automatisk körning är nu verifierad: samma record gav `apply OK` och `Applied` 2026-08-31 kl. 01:35:03 CEST. Ingen ny engångstriggning gjordes; ett separat fel för samma äldre record finns fortfarande kvar.

## Hotfixar och kontroller

- Första .NET 10-imagen startade inte eftersom statiska `RecurringJob.AddOrUpdate` användes innan Hangfires storage initierats. Känd legacy-compose/image återställdes via Dockhand. PR #119 bytte till DI-upplöst `IRecurringJobManager`; jobb-ID:n, cron, tidszoner och handlers behölls.
- Den därefter stabila canaryn gav `/api/session` 500 eftersom antiforgery såg intern HTTP i stället för publik HTTPS. PR #120 lade till explicit proxykonfiguration och tidig `UseForwardedHeaders()`.
- Proxy-hotfixen ändrade inte databasmodell, legacyalgoritm, ONECTA-payload eller återkommande jobbtider.

Slutliga kodkontroller för PR #120:

- Release build via installerad .NET 10.0.111 SDK: `dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll" Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal` – godkänd. Första bygget fångade tvetydig `IPNetwork`; explicit alias till `System.Net.IPNetwork` rättade felet.
- Fokuserat VSTest-target med `FullyQualifiedName~TrustedProxyForwardingTests` – 3 godkända, 0 misslyckade.
- Fullt VSTest-target på Release-build – 521 godkända, 9 befintligt överhoppade, 0 misslyckade (530 totalt).
- `npm run build` i `frontend` – TypeScript/Vite godkända.
- `git diff --check` – godkänd.
- PR #120 – samtliga sju GitHub-checkar gröna, inga reviewkommentarer. CI använder låst SDK 10.0.400; lokal SDK-avvikelse är redovisad.
- Containerworkflow `33331521424` – grön publicering från exakt merge-revision `233afa4...`; digest hämtades från `containerimage.digest` i CI-loggen.

De nio skipparna är befintliga tester som kräver nätverk eller saknar full HTTP-testinfrastruktur. Ett fullständigt inloggat browser-E2E har inte genomförts eftersom Daikin-ID-sidan kräver användarens inloggning.

## Verifierad drift

- Aktiv fil: `/mnt/user/appdata/dockhand/stacks/daikin/compose.yaml`, åtkomlig i Dockhand som `/mnt/stacks/daikin/docker-compose.yml` via befintlig symlänk. Publik adress `https://daikin.widsell.nu`, intern appport 5000.
- Aktiv Compose var byte-identisk mot den kända canarykandidaten före sista uppgraderingen. Endast appdigest och `Security:TrustedProxyNetworks` ändrades.
- Runtime-label visade revision `233afa4...`; app, PostgreSQL och EMHASS körde med noll omstarter. Appstart 19:46:25 UTC. pgAdmin var fortsatt avstängd.
- Containrar: `daikin-prisstyrning-1`, `daikin-postgres-1` (`postgres:17-alpine`) och `daikin-emhass-1`. Inga hostportar publicerade. pgAdmin ligger bakom profilen `admin`.
- EMHASS: `ghcr.io/davidusb-geek/emhass:v0.18.0@sha256:b9c88442c2623c83469cb6ae103991a349cc63fbd5c8fd100d5e071e6ff41204`.
- `daikin_thermal-internal` har `internal=true`, endast appen och EMHASS som medlemmar. Separata `daikin_emhass-egress` har endast EMHASS som medlem. EMHASS har ingen publicerad port.
- `/health/live`, `/health/ready` och anonym `/api/session`: HTTP 200. Sessionen var `authenticated=false`; CSRF-cookie hade `Secure`/`HttpOnly`. Token/cookievärden skrevs inte ut.
- Anonym `/api/thermal/status`: HTTP 401.
- Browser: skyddad inloggningsvy renderades; knappen gick till ordinarie Daikin/ONECTA-ID-inloggning. Ingen anläggningsdata visades anonymt.
- Databas: en thermal-konfiguration, `Legacy/Legacy`, noll thermal-styrkommandon.
- `Thermal:AllowLwtActive=false`, `Thermal:AllowFullActive=false`, `Thermal:EnableDhwWriterCoordination=false`, `Daikin:ApplySchedule=true`, `Emhass:Enabled=false`.
- `Security:TrustedProxyNetworks=172.19.0.0/16`: verifierat Traefik-nät; endast client-IP och HTTPS-schema, ett proxyhopp.
- Credentials: en tokenrad, krypterade kolumner ifyllda och legacykolumner bevarade för rollback. `PreserveLegacyDaikinTokenColumns=true` är kvar.
- Globala HA-miljövariabler har tagits bort. Noll HA-anslutningar är sparade i den nya kontobundna modellen.
- Verifieringsskrivning: befintligt `schedule-update-job-noon` engångstriggades genom Hangfires vanliga Basic-autentisering och CSRF-skydd, svar HTTP 204. Ordinarie schema ändrades inte.
- Jobbet behandlade två auto-apply-record: ett äldre record utan token fick `Apply failed`; recordet med pumpens credential fick `apply OK` och `Applied` kl. 19:52:54 UTC. Inga kontoinställningar ändrades för att dölja felet.

### Read-only-uppföljning 20:42 UTC / 22:42 CEST

- App, PostgreSQL och EMHASS kör fortsatt med noll omstarter; app-/EMHASS-digests är oförändrade.
- `/health/live`, `/health/ready` och anonym `/api/session` gav 200; sessionen var oautentiserad och hade utfärdad CSRF-token. `/api/thermal/status` gav fortsatt 401 utan session. Inga token- eller cookievärden skrevs ut.
- Databasen innehöll fortsatt en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Ingen senare schemakörning fanns i de kontrollerade loggarna efter 20:10:03 UTC. Nästa ordinarie tid enligt det oförändrade schemat är 2026-08-31 01:35 CEST / 2026-08-30 23:35 UTC; den har inte observerats ännu.
- Den driftsatta PR #120:s sju checkar bekräftades fortsatt gröna. De avser den driftsatta revisionen, inte den nya lokala uppföljningen.
- Ingen deploy, ny testsändning, kontoändring eller aktivering gjordes. Lokala förbättringar och deras separata tester finns i [uppföljningsrapporten](2026-08-30-session-recovery-regressions.md).

### Read-only-uppföljning 21:41 UTC / 23:41 CEST

- App, PostgreSQL och EMHASS kör fortsatt med noll omstarter och samma digests. Ingen deploy eller ändring av stacken gjordes.
- `/health/live`, `/health/ready` och anonym `/api/session` gav 200; sessionen var oautentiserad med utfärdad CSRF-token. `/api/thermal/status` gav 401. Inga token- eller cookievärden skrevs ut.
- Databasen hade fortsatt en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Ingen senare `ScheduleUpdate`-/`apply OK`-post fanns i de kontrollerade loggarna sedan 20:42 UTC. Ordinarie 01:35-körning var fortfarande framtida; ingen extra testsändning gjordes.
- Fortsatt lokal implementation gäller HTTP-säkerhet, adminlistning, utloggningsåterkoppling och navigation. Den separata [kodverifieringen 2026-08-31](2026-08-31-account-http-verification.md) redovisar 566 godkända backendtester, 6 kvarvarande undantag, 17 UI-tester och 10 tillämpliga E2E-flöden. Dessa ändringar är inte driftsatta och resultaten ska inte sammanblandas med produktionens verifiering ovan.

### Ordinarie legacykörning – verifierad 2026-08-31 02:43–02:45 CEST

- Oförändrade app-/EMHASS-digests; app, PostgreSQL och EMHASS körde med noll omstarter.
- Hälsa och anonym session gav 200, `authenticated=false` och skyddad thermal-status gav 401. Inga hemliga svarsfält skrevs ut.
- En uttryckligt read-only databastransaktion bekräftade `Legacy/Legacy` och noll thermal-styrkommandon.
- Den ordinarie 01:35-körningen gav `apply OK` 01:35:03.631 CEST och `Applied` 01:35:03.645 CEST. Ett separat `Apply failed` fanns 01:35:01 CEST.
- En jämförelse mot loggfönstret för föregående engångsverifiering bekräftade att både det lyckade och det misslyckade recordet är samma som tidigare. Inga kontoinställningar, credentials eller scheman ändrades och inget jobb utlöstes.
- Lokala rumsvy-/kvalitetsrättningar och separata testresultat redovisas i [uppföljningen 2026-08-31](2026-08-31-room-quality-verification.md). Dessa är inte publicerade eller driftsatta. En kvarvarande brist i statusradens samlade datakvalitet är dokumenterad där för nästa koduppföljning.

### Read-only-uppföljning 2026-08-31 omkring 03:49 CEST / 01:49 UTC

- App, PostgreSQL och EMHASS körde fortsatt med noll omstarter och oförändrade digests.
- Hälsokontroller och anonym session gav 200, sessionen var oautentiserad och skyddad thermal-status gav 401. Endast HTTP-status och boolesk sessionsinformation visades, inga token- eller cookievärden.
- En uttryckligt read-only databastransaktion bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster observerades från 00:43:15 UTC till kontrollen. Den ordinarie 01:35 CEST-körningen ovan är tidigare verifierad evidens; inget nytt jobb utlöstes.
- Den tidigare statusradsbristen är rättad i lokal källkod tillsammans med klientens översättning av verkliga numeriska API-enumvärden. [Separat verifieringsrapport](2026-08-31-status-quality-verification.md) redovisar 671 godkända backendtester (6 befintliga undantag), 98 UI-tester och 14 tillämpliga browserflöden. Dessa revisioner är inte publicerade eller driftsatta och ändrar ingen aktiv regulator eller legacyalgoritm.
- Inga kontoinställningar, credentials, scheman, containrar eller aktiveringsspärrar ändrades.

## Rollback

Vanlig rollback görs i samma Dockhand-stack utan databasåterläsning. Behåll de additiva tabellerna och krypteringsnyckeln.

- Ursprunglig legacy-compose i Dockhand: `/mnt/stacks/daikin/backups/docker-compose.pre-1d556847-20260830T143508Z.yml`.
- Ursprunglig apprevision: `9957d28f37c45d98ceca8b32397143b5cd7ae0f0`; lokal image-ID: `sha256:359e67bf72b412bb4c4d6a9f518880d22dea8c56da8f7dd2d9d50d598afe9230`.
- Närmast föregående .NET 10-compose: `/mnt/stacks/daikin/backups/docker-compose.pre-233afa4-20260830T194500Z.yml`; appdigest `sha256:f03e6d7dbb3bb06e1e44287241d5e640268b7c8f6690a84194a5606eebc89787`. Den startar legacyjobben men saknar proxyfixen för inloggningen.
- Båda Compose-backuperna verifierades med rättighet `0600`.
- Separat PostgreSQL-katastrofbackup: `/mnt/stacks/daikin/backups/pre-1d556847-20260830T143508Z.dump`, skyddad med `0600` och validerad med `pg_restore -l`. Återläs inte den vid vanlig imagerollback eftersom senare data då förloras.
- Credentialnyckeln på värden: `/mnt/user/appdata/dockhand/stacks/daikin/secrets/credential_encryption_key`, monterad som Docker-secret, rättighet `0600`. Skyddad backup finns i stackens backupkatalog. Nyckelvärdet får aldrig dokumenteras eller checkas in. Bevara filen även vid imagerollback.

## Återstående arbete

- Användaren behöver logga in med samma Daikin/ONECTA-konto och prova kontosidorna. Den nya signerade sessionen återanvänder inte gamla osignerade cookies.
- Spara/testa kontobunden HA-anslutning och entity-mappning innan telemetri/modellverifiering kan börja. Ingen HA-token har tilldelats ett gissat konto.
- Ordinarie legacykörning 2026-08-31 01:35 CEST är verifierad read-only med accepterad API-skrivning. Fortsätt följa kommande ordinarie körningar när de finns; en fysisk DHW-cykel är ännu inte verifierad här.
- Utred det äldre auto-apply-recordet utan token först när kontoansvar är verifierat; ändra inte användarinställningar implicit.
- Uppstarten skriver en icke-fatal varning om saknad `libgssapi_krb5.so.2`; migration, databas-readiness och legacy-skrivning fungerar. Hantera separat, inte som ett aktuellt driftstopp.
- Säkerhetsuppföljning: en tidigare rå verktygsvy från Compose-redigeraren återgav credentialvärden i uppgiftsloggen. Värdena finns inte i denna dokumentation eller i hotfix-committen. Rotera berörda HA-/Daikin OAuth-credentials via respektive tjänsts normala flöde och uppdatera skyddad driftkonfiguration under kontrollerade former. Ingen rotation eller återkallelse har genomförts här.
- Full intelligent styrning är inte driftgodkänd: verkliga Shadow-dagar, värmekurvetest, komfort-/modellmått och DHW-cykelverifiering enligt planen återstår före aktiv styrning.

## Dokumentationsspärr och timvis fortsättning

Windows Kontrollerad mappåtkomst blockerade ändring av det gemensamma projektets `README.md`/`INFRASTRUCTURE.md` under Dokument, även vid godkänd eskalering. Defender-händelse 1123 verifierade att `codex.exe` blockerades; skyddet har inte ändrats eller kringgåtts.

Rapporten sparades därför i kodrepositoryts `plans`-katalog. Den färdiga patchen `2026-08-30-shared-infrastructure-update.patch` ligger bredvid för senare godkänd uppdatering av det gemensamma projektet. De gemensamma dokumenten är ännu inte uppdaterade.

Den befintliga timvisa fortsättningen är aktiv och har uppdaterats till återstående implementation/verifiering och read-only-produktionsuppföljning. Den ska inte upprepa deploy eller testsändning varje timme. `Legacy/Legacy` och aktiveringsspärrarna ligger kvar.
