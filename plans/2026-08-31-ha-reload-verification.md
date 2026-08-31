# Kontobunden HA-omladdning – verifiering 2026-08-31

## Resultat och avgränsning

Fortsättning från `11af2c2` på `codex/session-recovery-regressions`. Omladdning av kontots Home Assistant-anslutning och dess UI-status är implementerad och verifierad **lokalt**, inte publicerad eller driftsatt. Befintlig legacyalgoritm, Hangfire-jobb, ONECTA-payload, styrklient, regulator, aktiveringsspärrar och databasschema är oförändrade.

Den gamla managern jämförde bara aktiverade konto-ID:n. En sparad adress/token/inställning lämnade därför den gamla socketen igång. Den markerade också anslutningen som lyckad innan HA bekräftat prenumerationen och hämtade startbild före prenumerationen, vilket lämnade en lucka för händelser. REST-startbilden kunde dessutom läsa en annan konfigurationsrevision än socketen.

## Implementerat

- Varje sparning får en monoton revision med PostgreSQL-kompatibel mikrosekundprecision. Sparning/radering serialiseras per konto **inom den enda appinstansen**, så två flikar inte kan vända ordningen mellan databascommit och cacheinvalidering. Ingen token, tokenhash eller tokenlängd används som revisionsidentifierare.
- Efter lyckad commit töms det berörda kontots cache och gamla callbacks förlorar sin generationsbundna skrivrätt. Andra konton påverkas inte. Nekad sparning/radering lämnar anslutningen kvar. Radering/inaktivering hindrar gamla arbetare från att återinföra data; en senare ny konfiguration kan starta igen.
- En sammanslagen notifiering väcker anslutningsmanagern efter sparning. Polling var 30:e sekund återhämtar även missade notifieringar och avslutade arbetare. Ändrade eller inaktiverade arbetare avbryts, inväntas och frigörs. Ett uppstartsrace mellan databascommit och cacheinvalidering hanteras även när revisionen redan matchar och inga nya sensorhändelser kommer.
- En missad databasuppdatering eller en trasig HA-anslutning får inte stoppa legacyjobben. Återförsök använder exponentiell väntetid upp till 60 sekunder plus mindre än en sekund jitter. En refuserad äldre revision självnotifierar inte i en snabb loop.
- Autentisering och prenumerationsbekräftelse måste lyckas för rätt meddelande-ID innan REST-startbilden hämtas. Kontroll av `result`, `id` och `success` följer [Home Assistants dokumenterade WebSocket-protokoll](https://developers.home-assistant.io/docs/api/websocket/). HA-token används endast för det sparade kontots autentisering; inga HA-serviceanrop tillkommer.
- Socket och REST-startbild använder samma upplösta adress, telemetriidentitet och revision. Sökvägsprefix och port bevaras. REST-/historikklientens kontobundna metoder finns kvar.
- Efter bekräftad prenumeration tas händelser emot medan REST kör. De slås samman med hela startbilden atomiskt, utifrån HA:s tid. Äldre händelser rullar inte tillbaka nyare värden, och borttagna entities återuppstår inte genom ett försenat äldre event. Ingen delvis inläst startbild exponeras.
- Uppstart inklusive anslutning, autentisering, prenumeration och REST-startbild har en 30-sekunders avbrytning. Socketen använder keepalive 30 sekunder och pong-timeout 15 sekunder. Textmeddelanden begränsas till 1 MiB och senaste buffrade händelse till högst 20 000 entity-ID:n per session; binära meddelanden och oväntade protokollsvar godkänns inte.
- Worker- och REST-testloggning innehåller endast fasta generiska feltexter, inte exceptiontext, servertext, URL:er, tokenvärden, fragment eller längder. Diagnostikens detaljnivå är avsiktligt begränsad; ytterligare säkra felkoder kan införas separat.
- `GET /api/home-assistant/status` behåller sina fält och får additiva `phase` och `configurationUpdatedAtUtc`. Den verkliga, kontoskyddade HTTP-routen testas utan integrationer. Gammal revisionsdata kan aldrig rapporteras som ansluten, även om ett sent svar togs emot efter sparningen. Saknad/inaktiv anslutning döljer kvarvarande cache.
- UI skiljer **Anslutning sparad** från **Liveansluten**, och visar anslutning, ny startbild, återförsök, inaktivering och läsfel separat. REST-testet beskrivs uttryckligen som något annat än WebSocket-/sensorverifiering. Förhandsvisningen förklarar cachetömning och att sensormappning, legacy-DHW och driftläge inte ändras.
- Sparning/radering avbryter gamla klientfrågor, tömmer den gamla katalogen och återställer tidigare testbesked. Ett sent äldre statusbesked avvisas genom exakt revisionsmatchning. Ny katalog hämtas först när aktuell anslutning bekräftats. **Uppdatera anslutningsstatus** gör bara GET, aldrig omstart, testsändning eller inställningsändring.
- Livekortets godkännande åldras även när polling inte levererar: läsfel, okänt format, en annan revision eller ett statusbesked äldre än två minuter ger inget grönt besked. Mobilkortet staplar fälten, har namngiven region, statusmeddelande och tangentbordsåtkomlig återhämtning.

## Kodverifiering

| Kontroll | Resultat |
|---|---|
| .NET 10 Release-bygg med `ContinuousIntegrationBuild=true` | Godkänt; inga rapporterade varningar/fel |
| Riktad backend `FullyQualifiedName~HomeAssistant`, slutlig körning | **144/144**, inga undantag |
| Hela backendens Release-svit | **804 godkända, 6 befintliga undantag, 0 fel**, totalt 810 |
| Riktad Vitest `homeAssistantConnectionStatus HomeAssistantLiveStatus ThermalSettingsPage` | **26/26** |
| `npm.cmd test` | **162/162**, 14 testfiler |
| `npm.cmd run build` och byggena i `test:e2e` | TypeScript/Vite godkända |
| `npm.cmd run test:e2e -- ha-connection-reload.spec.ts` | **2/2**, desktop och mobil |
| `npm.cmd run test:e2e` | **20 godkända, 6 befintliga projektexkluderingar, 0 fel**, totalt 26 |
| `git diff --check` | Godkänt; normala LF/CRLF-notiser |

51 nya backendfall och 24 nya UI-fall tillkommer jämfört med kataloguppföljningens 753/6 och 138. Den riktade HA-sviten kördes först med 123 respektive 138 fall innan samtliga nya tester var tillagda; slutlig riktad körning ovan omfattar 144.

Backendkommandon från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /restore /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /p:VSTestTestCaseFilter=FullyQualifiedName~HomeAssistant /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontendkommandona i tabellen kördes från `frontend`. Repository/CI/Docker är fortfarande låsta till SDK 10.0.400; lokalt användes installerade 10.0.111 uttryckligen. Ingen pin ändrades. Inga nya CI-, publish-, containerbyggen eller fulla applikationsstarter gjordes.

Webbläsarflödet sparar enbart syntetiska inställningar: ett enda mockat `PUT /api/home-assistant/config` med ändrad åldersgräns och tomma tokenfält. Det provar ett sent gammalt statusbesked, omladdning, ny startbild, anslutning, läsfel och tangentbordsåterhämtning. Inga lägesbyten, imports, anslutningstester eller styrkommandon skickas. Fyra bilder av livekortets startbilds-/felläge granskades visuellt, inklusive 320-pixelslayout utan horisontell överströmning. Bilderna är ignorerade testartefakter, inte produktionsacceptans.

Tidiga UI-körningar fångade två fel som rättades innan helsviterna blev gröna: Windows kunde förväxla hjälpmodulens och komponentens basnamn, och Axe fann en hoppad rubriknivå i anslutningspanelen. Hjälpmodulen fick ett entydigt namn och rubriknivåerna rättades; ingen tillgänglighetsregel stängdes av för dessa fel. JSDOMs befintliga undantag för färgkontrast är kvar.

De sex backendundantagen är fortsatt två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. EF InMemory, simulerad WebSocket/HTTP och syntetiska konton ersätter inte PostgreSQL-, verklig HA-/nätverks-, full startup- eller OAuth-acceptans. Verklig keepalive/pong-beteende och PostgreSQLs tidsrundning är inte end-to-end-provade här. Full skärmläsar- och kontrastkontroll återstår separat.

## Produktionskontroll – separat evidens

Read-only verifierat **2026-08-31 omkring 07:19 CEST / 05:19 UTC**:

- App, PostgreSQL och EMHASS kör med noll omstarter och samma image-referenser som i [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session`: HTTP 200. Sessionen var oautentiserad; endast boolesk CSRF-information återgavs.
- Anonym `/api/thermal/status`, `/api/home-assistant/status` och `/api/home-assistant/entities`: HTTP 401.
- Explicit `BEGIN TRANSACTION READ ONLY` bekräftade en thermal-konfiguration i `Legacy/Legacy` och noll termiska styrkommandon.
- Inga nya `apply OK`/`Applied`/`Apply failed`-poster mellan **04:00:00 och 05:19:28 UTC**. Tidigare accepterad ordinarie skrivning 01:35 CEST är redan dokumenterad. Ingen ny testsändning eller schemakörning utlöstes.
- Ingen deploy, containerändring, kontoändring, credentialändring eller aktivering gjordes. Inga råloggar, credential-/tokenvärden, fragment, längder eller cookie-/sessionsvärden visades.

## Nästa avgränsning och kvarvarande acceptans

1. Den avgränsade insamlings-/historik-/telemetrikvalitetsuppföljningen är nu lokalt verifierad i [sensorrapporten](2026-08-31-sensor-validation-verification.md). Katalogbedömning och en lyckad prenumeration är inte i sig godkänd sensorkvalitet eller styrberedskap; övriga modell- och dygnsgrindar återstår enligt den rapporten.
2. Granska den samlade lokala serien och kör tillämplig CI före en motiverad uppdatering av samma Dockhand-stack. Den lokala adminraderingsspärren är ännu inte driftsatt; använd inte den gamla produktionsraderingen. Ingen separat stack och ingen aktivering ingår.
3. Verklig kontoinloggning, kontoägd HA-konfiguration, nätverksavbrott/återanslutning och husets Shadow-, modell-, värmekurve- och DHW-acceptans återstår. Ett accepterat ONECTA-anrop är inte en verifierad fysisk varmvatten- eller hygiencykel.
4. Inställningslåset och cachen är processlokala, avsedda för den befintliga enda appinstansen. Horisontell skalning kräver databasbaserad skrivkonkurrens och notifiering mellan instanser; det är inte implementerat eller godkänt här.

Huvudplanen, produktionsrapporten och föregående katalograpport är uppdaterade med denna uppföljning. Gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument är fortsatt oförändrade på grund av Kontrollerad mappåtkomst; den tidigare förberedda patchen finns kvar och skyddet har inte ändrats eller kringgåtts. Meningsfullt godkänt arbete återstår; den timvisa fortsättningen har inte ändrats i denna uppföljning.
