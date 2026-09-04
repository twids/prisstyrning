# Prisstyrning – driftsättningsverifiering 2026-08-30

## Resultat

Den befintliga Dockhand-stacken `daikin` är uppgraderad till .NET 10-revision `233afa4c7d8a2784ce7e80179e0b7df8534f34da` (PR #120). Ingen parallell stack skapades. Legacy är ensam DHW-writer; LWT/FullActive är inte aktiverade.

Appimage: `ghcr.io/twids/prisstyrning@sha256:9e0fe803de6cda3e7154459eb0afc0a3743f3455b0a4c6aaa24991bd39e9e22d`.

Verklig ONECTA-skrivning lyckades kl. 19:52:54 UTC / 21:52:54 CEST. Loggen innehöll både `apply OK` från legacy-skrivvägen och `Applied` från `ScheduleUpdateHangfireJob`. Detta bevisar accepterad API-skrivning, inte en fysisk uppvärmningscykel.

Även ordinarie automatisk körning är nu verifierad: samma record gav `apply OK` och `Applied` 2026-08-31 kl. 01:35:03 CEST. Ingen ny engångstriggning gjordes; ett separat fel för samma äldre record finns fortfarande kvar.

Senare read-only-kontroll omkring 17:35 CEST bekräftade oförändrad drift. Ordinarie 13:35-jobb slutfördes, men recordet med tidigare lyckad skrivning hade ingen schemaläggbar åtgärd och gjorde ingen ny skrivning. Det äldre separata recordets `Apply failed` kvarstod. Se den tidsstämplade uppföljningen nedan; detta ersätter inte den tidigare accepterade skrivningens evidens.

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

### Read-only-uppföljning 2026-08-31 omkring 04:53 CEST / 02:53 UTC

- Oförändrade image-referenser; app, PostgreSQL och EMHASS körde fortsatt med noll omstarter.
- Hälsa och anonym session svarade 200, sessionen var oautentiserad och skyddad thermal-status gav 401. Endast statuskoder och boolesk sessionsinformation visades.
- En uttrycklig read-only databastransaktion bekräftade `Legacy/Legacy` för en thermal-konfiguration och noll thermal-styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster fanns från 01:49:10 UTC till 02:53:04 UTC. Inget extra jobb eller testsändning utlöstes.
- Den gamla adminraderingen reproducerades med isolerade testkonton och spärrades i **lokal källkod**. Den gav felaktigt `deleted=true` trots kvarvarande sessioner, HA-koppling och auto-apply. [Separat rapport](2026-08-31-admin-account-safety-verification.md) redovisar säkerhetsavgränsning, admin-UX och 688/6 backendtester, 108 UI-tester samt 16 tillämpliga browserflöden. Spärren är inte driftsatt; använd inte den gamla kontoraderingen i produktion.
- Ingen produktionstoken, kontoinställning, behörighet, historik, container eller aktiveringsspärr ändrades. Ingen koppling mellan kodfelet och det äldre tokenlösa produktionsrecordet har verifierats.

### Read-only-uppföljning 2026-08-31 omkring 06:00 CEST / 04:00 UTC

- App, PostgreSQL och EMHASS körde fortsatt med noll omstarter och oförändrade image-referenser.
- Hälsa och anonym session gav 200; sessionen var oautentiserad. Anonym thermal-status och `/api/home-assistant/entities` gav båda 401. Endast statuskoder och boolesk sessionsinformation visades.
- En explicit read-only databastransaktion bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster observerades från 02:53:04 UTC till 04:00:00 UTC. Ingen extra schemakörning, testsändning eller deploy gjordes.
- [Separat lokal kodrapport](2026-08-31-ha-catalog-verification.md) redovisar korrigerad preliminär HA-katalogkvalitet, gemensam säker sensorväljare och 753/6 backendtester, 138 UI-tester samt 18 tillämpliga browserflöden. Fixarna är **inte driftsatta** och ändrar inte legacy, regulator eller sensorernas exkluderingsräknare.
- Inga konton, credentials, behörigheter, containerinställningar eller aktiveringsspärrar ändrades. Revisionsstyrd omladdning av HA-anslutningen och fortsatt insamlingsvalidering är dokumenterade som nästa kodarbete.

### Read-only-uppföljning 2026-08-31 omkring 07:19 CEST / 05:19 UTC

- Oförändrade image-referenser för app och EMHASS; app, PostgreSQL och EMHASS körde med noll omstarter.
- Hälsa och anonym session svarade 200, sessionen var oautentiserad med booleskt verifierad CSRF-utgivning. Anonym thermal-status, HA-status och HA-katalog gav samtliga 401. Inga sessions-/cookievärden visades.
- Explicit read-only databastransaktion bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster mellan 04:00:00 och 05:19:28 UTC. Ingen extra schemakörning, testsändning eller deploy gjordes.
- [Separat lokal kodrapport](2026-08-31-ha-reload-verification.md) redovisar revisionsbunden HA-återanslutning, cache-/konkurrensskydd, säker loggning, live-status-UX och 804/6 backendtester, 162 UI-tester samt 20 tillämpliga browserflöden. Ändringarna är **inte driftsatta**; regulator, legacy och aktiveringsspärrar är oförändrade.
- Ingen produktionskonfiguration, behörighet, credential eller kontoägare ändrades. Nästa kodavgränsning gäller insamlingskedjans sensor-/tidsvalidering, inte ny deploy eller aktivering.

### Read-only-uppföljning 2026-08-31 omkring 10:07 CEST / 08:07 UTC

- App, PostgreSQL och EMHASS körde med noll omstarter och oförändrade image-referenser. Inga containrar ändrades.
- Hälsa och anonym session gav 200; sessionen var oautentiserad med booleskt verifierad CSRF-utgivning. Anonym thermal-status, HA-status och HA-katalog gav 401. Inga sessions-/cookievärden visades.
- Explicit read-only databastransaktion bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-markörer från 05:19:28 till 08:07:44 UTC. Ingen testsändning, schematrigger eller deploy gjordes.
- Den föregående HA-omladdningen sparades som lokal commit `c67f1d1`. [Sensorvalideringsrapporten](2026-08-31-sensor-validation-verification.md) beskriver nästa lokala kodleverans: säker sensor-/tidsvalidering, komplett historikanrop, striktare telemetrikrav, lägesguidens felåterhämtning/mobilvy och 871/6 backendtester, 171 UI-tester samt 22 tillämpliga browserflöden. Inget av detta är driftsatt.
- Inga kontoinställningar, kontoägare, credentials, rättigheter eller aktiveringsspärrar ändrades. Fortsatt arbete och kvarvarande modell-/verklighetsacceptans är separata från den friska Legacy-driften.

### Read-only-uppföljning 2026-08-31 omkring 11:13–11:17 CEST / 09:13–09:17 UTC

- App, PostgreSQL och EMHASS körde med noll omstarter och oförändrade image-referenser.
- Hälsa och anonym session gav 200 med oautentiserad session och booleskt verifierad CSRF-utgivning. Anonym thermal-status, HA-status och HA-katalog gav 401.
- Explicit read-only databastransaktion bekräftade en konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-markörer från 08:07:44 till 09:15:15 UTC. Inget extra jobb eller någon deploy gjordes.

### Ordinarie 13:35-jobb och read-only-uppföljning 2026-08-31 omkring 17:35 CEST / 15:35 UTC

- App, PostgreSQL och EMHASS körde med samma image-referenser och noll omstarter. Hälsa och anonym session gav 200, sessionen var oautentiserad med verifierad CSRF-utgivning. Anonym thermal-status, HA-status och HA-katalog gav 401.
- Explicit read-only databastransaktion bekräftade en konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- 13:35-jobbet startade 11:35:02.493 UTC och slutfördes 11:35:02.893 UTC: två behandlade record, noll fångade jobbundantag. Recordet med tidigare lyckad skrivning hade `generated=False` och ingen schemaläggbar åtgärd. Detta är **inte en ny accepterad skrivning**.
- `Apply failed` 11:35:02.791 UTC matchade samma äldre misslyckade record som vid 01:35. Jämförelsen redovisade endast boolesk matchning, inga konto-ID:n. Noll jobbundantag betyder inte noll misslyckade skrivförsök. Inga konto-/credentialändringar gjordes för att dölja felet.
- Inga `apply OK`/`Applied`-markörer från 09:15:15 till 15:36:19 UTC. Ingen testsändning, schematrigger, omstart, deploy eller aktivering gjordes. Den tidigare accepterade 01:35-skrivningen är fortsatt tidigare evidens, inte verifiering av en fysisk DHW-cykel.
- [Separat lokal modellrapport](2026-08-31-model-evidence-verification.md) redovisar kompletta valideringsfönster, striktare tränings-/uppvärmningsdygnsevidens, säker modell-UX och **980/6 backendtester, 200 UI-tester och 24 tillämpliga browserflöden**. Inget av detta är publicerat eller driftsatt. Modellkonsumenter, revisionsbunden proveniens och verklighetsacceptans återstår.
- Endast sanerade statusar/resultat visades, inga hemligheter eller råloggar. Ett citeringsfel i första skrivskyddade Docker-inspektionens utdataformat rättades före lyckad avläsning; inga containrar ändrades.

### Lokal kodverifiering 2026-08-31 – ingen ny driftåtgärd

- [Modellkonsument- och körapporten](2026-08-31-planning-model-consumption-verification.md) redovisar gemensam modellbedömning före/efter optimering, revisionssnapshot för beräkningsunderlag, säker request-/lease-hantering och tydligare händelsehistorik på svenska.
- Slutlig lokal verifiering: **1 029 godkända backendtester/6 befintliga undantag, 207 UI-tester och 26 tillämpliga browserflöden/6 projektexkluderingar**. .NET 10 Release och TypeScript/Vite är godkända. Inga fel i slutkörningarna.
- Ingen ny produktionsavläsning, publicering, deploy, kontoförändring, testsändning eller aktivering ingick. Ovanstående 17:35-avläsning är tidigare driftbevis, inte ny verifiering i denna leverans. Alla nya ändringar är lokala och Legacydriften har inte ändrats av arbetet.
- Historisk träningsproveniens, slutlig plan-/skrivvalidering, PostgreSQL-konkurrensprov och fysisk Shadow/DHW-acceptans återstår; intelligent styrning är inte driftgodkänd.

### Lokal kodverifiering 2026-09-01 – solver, aktiv plan och styrtelemetri

- [Solver-/plan-/kontrollrapporten](2026-09-01-solver-plan-control-verification.md) redovisar fail-closed validering av full EMHASS-horisont och tidsaxel, lagrad aktiv plan, modell-/konfigurationsfingerprint, aktuellt plansteg samt kvalitetsmarkerad rum-, flödes-, DHW- och avfrostningstelemetri.
- Slutlig lokal verifiering: **1 101 godkända backendtester/6 befintliga undantag, 207 UI-tester och 26 tillämpliga browserflöden/6 projektexkluderingar**. .NET 10 Release och TypeScript/Vite är godkända; `git diff --check` är godkänt.
- Ingen produktionsavläsning, publicering, deploy, appstart, migration, testsändning, konto-/credentialändring eller aktivering ingick. Senaste produktionsbeviset ovan är tidigare evidens och ska inte beskrivas som en ny kontroll.
- `Legacy/Legacy` är fortsatt det enda driftgodkända läget. PostgreSQL-/HA-konkurrens, full planeringsdataproveniens och verklig Shadow/DHW/hygienacceptans återstår före LWT eller FullActive.

### Lokal kodverifiering 2026-09-01 – termisk modellprovenans

- [Modellprovenansrapporten](2026-09-01-thermal-model-provenance-verification.md) redovisar additiv `jsonb`-lagring och SHA-256-bindning till exakt kontoägt 2R2C-/COP-träningsurval, aktiverad konfiguration, urvalsregel och logisk algoritmversion. Äldre modeller utan bevis blir ej verifierade tills de tränats om.
- Slutlig lokal verifiering: **1 157 godkända backendtester/6 befintliga undantag, 221 UI-tester och 26 tillämpliga browserflöden/6 projektexkluderingar**. .NET 10 Release och TypeScript/Vite är godkända; migrationsdrift och `git diff --check` är godkända.
- Ingen produktionsavläsning, publicering, deploy, appstart, körd migration, testsändning, konto-/credentialändring eller aktivering ingick. Den tidigare verifierade Legacy-produktionen ovan är historiskt driftbevis, inte en ny kontroll i detta pass.
- Beständigt källbevis är nu lokalt implementerat, men automatisk återhashning av ändrad råhistorik, build-digestbindning, verklig PostgreSQL-konkurrens och fysisk Shadow-/DHW-/hygienacceptans återstår. `Legacy/Legacy` är fortsatt det enda driftgodkända läget. Den historiska punkten om automatisk återhashning slutfördes senare i [källomvalideringsrapporten](2026-09-01-thermal-model-source-revalidation.md); övriga gränser kvarstår.

### Lokal kodverifiering 2026-09-04 – körande kodrevision

- [Byggproveniensrapporten](2026-09-04-thermal-build-provenance-verification.md) redovisar inbakad källkodsrevision, modellbevis schema 2, `BuildChanged`-spärrar genom planering/readiness/skrivgräns och svensk omtränings-UX. Gammalt eller revisionsavvikande modellbevis kräver omträning; inga historiska rader skrivs om för att se godkända ut.
- Slutlig lokal verifiering: **1 198 godkända backendtester/7 explicit överhoppade, 233 UI-tester och 26 tillämpliga browserflöden/6 projektexkluderingar**. Release-ombyggnad, TypeScript/Vite, avsedda revisionsbyggspärrar och `git diff --check` passerade. Sju backendundantag inkluderar den fortfarande ej körda PostgreSQL-acceptansen.
- Container-/PR-workflowen har revisionsstämpling och containerattestering i källkoden. Ingen ny CI, publicering, signerad image eller digest verifierades; detta är inte release- eller aktiveringsbevis.
- Ingen produktionsavläsning, push, deploy, migrationskörning, konto-/credentialändring eller testsändning ingick. Den lokala Docker Desktop-enginens pipe saknades vid skrivskyddad kontroll; ingen engine eller container startades. Befintligt historiskt driftbevis ovan är inte upprepat och `Legacy/Legacy` är fortsatt det enda driftgodkända läget.

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

## Dokumentationsspärr och fortsättning varannan timme

Windows Kontrollerad mappåtkomst blockerade ändring av det gemensamma projektets `README.md`/`INFRASTRUCTURE.md` under Dokument, även vid godkänd eskalering. Defender-händelse 1123 verifierade att `codex.exe` blockerades; skyddet har inte ändrats eller kringgåtts.

Rapporten sparades därför i kodrepositoryts `plans`-katalog. Den färdiga patchen `2026-08-30-shared-infrastructure-update.patch` ligger bredvid för senare godkänd uppdatering av det gemensamma projektet. De gemensamma dokumenten är ännu inte uppdaterade.

Efter användarens senaste schemabegäran verifierades den befintliga fortsättningen som aktiv **varannan timme** i samma tråd. Den var redan rätt inställd; ingen dubblett eller onödig schemaändring gjordes. Prompten gäller återstående implementation/verifiering med bevarad Legacy och kräver uttryckligt godkännande före driftsättning eller aktivering. Upprepa inte deploy eller testsändning bara för att automationen körs. Produktionsuppföljning ska vara read-only; `Legacy/Legacy` och aktiveringsspärrarna ska bevaras.
