# Sensorvalidering och lägesguide – verifiering 2026-08-31

## Resultat och avgränsning

Den föregående, färdigtestade HA-omladdningen sparades först som lokal commit `c67f1d1` på `codex/session-recovery-regressions`. Denna fortsättning rättar sensor-/tidsvalidering genom normalisering, insamling, historik och telemetrikraven för beredskap samt förbättrar lägesguidens felhantering och mobilvy. Allt är **lokal kodverifiering**, inte publicering eller driftsättning.

Legacyalgoritm, schemalagda Hangfire-jobb, ONECTA-payloads, regulator, writer-handover, databasmodell, migrationer och aktiveringsflaggor ändras inte. Nya termiska lägen har inte aktiverats. Inga produktionskonton eller HA-credentials ändrades.

## Implementerat

- Numerisk normalisering avvisar `NaN`, oändlighet, flyttalsöverflöde efter enhetskonvertering och priser utanför decimalformatets lagringsområde. En temperatur/effekt med värdet 0 eller 1 blir inte en av/på-signal. Befintlig strikt tolkning av booleska texter är bevarad.
- Feltypade enhetsattribut ger ogiltig kvalitet i stället för undantag. Ett feltypat friendly name faller tillbaka till entity-ID. REST-listor och historik kan innehålla felaktiga enskilda JSON-poster utan att de övriga identifierbara posterna försvinner. En felaktig attributbehållare markeras uttryckligen, inte som korrekt tom metadata.
- Råtider kräver giltig HA-uppdateringstid och lokal mottagningstid. Saknade tider är otillgängliga; motsägelser/framtida tider över 30 sekunders klocktolerans är ogiltiga. En lokal datumsträng utan explicit tidszon får inte olika betydelse i Windows, Docker eller vid vintertidsbytet. Varken `last_changed` eller importens mottagningstid ersätter en saknad `last_updated`.
- Förändringstakt och senaste giltiga värde utgår från **mättiden**, inte tidpunkten då collectorn läste cachen. Därmed förlänger en försenad cacheread inte kritiska rummets 30-minutersgräns. Återhämtning kräver tre olika giltiga mättider; tre läsningar av samma cachevärde räcker inte. Ogiltighetsräknaren ökar högst en gång per femminutersbucket.
- Sensorhälsa är separat per konto och användning (rum/entity-roll). Nyare anslutnings-/anläggningsrevision återställer hälsotillståndet och gamla reservvärden; en äldre revisions bedömning får inte ersätta den nyare. Ny anslutning återanvänder inte en tidigare kritisk rumstemperatur som reservvärde.
- Collectorn läser en sammanhängande kontosnapshot och kräver aktuell anslutningsrevision, bekräftad liveanslutning och startbild. Frånkopplad eller ersatt cache ger inte giltig liveinsamling. Övriga givare fortsätter samlas när en enskild givare är felaktig. Kritiska reservvärden behåller ogiltig kvalitetsmetadata och kan inte godkänna beredskap.
- Avgiven värme och COP får inte bli icke-ändliga genom beräkning. COP publiceras bara när effekttecknet är bekräftat och elpatronsignalen uttryckligen är **av**, inte när signalen saknas. Väderprognosens tidsstämplar och enhetsformat kontrolleras också; okända enheter gissas inte vara °C eller m/s.
- HA-historikanropet begär fullständiga poster med attribut och uppdateringar. Den gamla koden skickade `minimal_response=false` och `no_attributes=false`, men HA aktiverar dessa alternativ genom **närvaron** av parametern. De utelämnas nu, och `significant_changes_only=0` bevarar även attributändringar. Det är verifierat mot [HA:s REST-dokumentation](https://developers.home-assistant.io/docs/api/rest/) och [HA Core, HistoryPeriodView](https://github.com/home-assistant/core/blob/dev/homeassistant/components/history/__init__.py). Ingen verklig HA-historik hämtades under denna verifiering.
- Historik använder en egen kvalitetsbedömare, samma åldersgräns som kontot, intervall-/förändringstaktskontroller och exkludering/återhämtning. Den påverkar inte livesensorernas räknare. Ett felaktigt värde bryter vidareföringen, långa luckor blir gamla och en otidsatt post gör den berörda tidslinjen oanvändbar i stället för att felet tyst överbryggas. Främmande entity-ID:n filtreras bort.
- Befintliga mätpunkter skrivs fortfarande aldrig över vid import. Även bevarade buckets bedöms i importens lokala kvalitetskedja, så en bevarad livepunkt inte gömmer ett historiskt fel för efterföljande importpunkter.
- Beredskapskontrollen skiljer ny insamling från godkänd insamling. Det nya kravet `telemetry-quality` kräver giltiga, ändliga värden och uttrycklig kvalitets-/exkluderingsmetadata för kritiska rum och obligatoriska värmegivare. Importer och reservvärden räknas inte som godkänd livekvalitet eller i 21-dagarstelemetritäckningen. Framtida eller konfigurationsäldre snapshots godkänns inte som aktuella. Detta är inte en fullständig revision av alla modell-/dygns-/DHW-grindar; se återstående arbete.

## UX/UI

- Lägesguiden förkastar gamla, felaktiga, ofullständiga eller fel-lägesbundna kontrollresultat. `ready=true` räcker inte om ett enskilt krav saknar godkännande. En klocka drar tillbaka godkännandet efter två minuter även när ingen ny pollning levererar.
- Ett läsfel eller en pågående ny kontroll stoppar aktiveringsknappen även i sista bekräftelsesteget. Tidigare gröna checklistor döljs och ett fast svenskt felmeddelande ersätter rå servertext. Knappen **Kontrollera kraven igen** gör bara en ny GET-kontroll.
- Varje krav har texten **Godkänt** eller **Åtgärd krävs**, inte bara färg. Stegindikatorn behåller inte en grön bock för ett återkallat godkännande. Avvisad lägesändring fångas och beskrivs utan falskt lyckat besked eller oobserverad promise.
- Guidad återgång till Legacy finns kvar vid telemetribortfall, i linje med serverns befintliga rollbackväg. Texten förklarar nollställning av LWT, återtagande av DHW och eventuell återställning av ONECTA-schemat. Inga serverspärrar eller skrivfunktioner har försvagats.
- Visuell kontroll fångade en klippt stegindikator och onödigt trång aktiveringsknapp på 320 px. Mobilguiden visar nu **Steg N av 3** och har fullbred huvudknapp under Avbryt/Tillbaka. Efter rättningen kontrollerades nya bilder av både sensorfel och läsfel.
- Historikpanelen förklarar att importerade punkter valideras separat, inte bekräftar aktuell liveinsamling eller verifierade Shadow-dygn och kan sakna användbar tidsstämplad historik.

## Kodverifiering

| Kontroll | Slutresultat |
|---|---|
| .NET 10 Release-bygg med `ContinuousIntegrationBuild=true` | Godkänt, inga rapporterade varningar/fel |
| Riktad backend `FullyQualifiedName~HomeAssistant` | **211 godkända, 0 undantag, 0 fel** |
| Hela backendens Release-svit | **871 godkända, 6 befintliga undantag, 0 fel**, totalt 877 |
| Riktad Vitest `ModeWizard` | **11/11** |
| `npm.cmd test`, omkörd efter mobilrättningen | **171/171**, 14 testfiler |
| `npm.cmd run build` samt bygget i sista E2E-körningen | TypeScript/Vite godkända |
| `npm.cmd run test:e2e -- readiness-recovery.spec.ts` | **2/2**, därefter omkört i helsviten efter mobilrättning |
| `npm.cmd run test:e2e`, slutlig helsvit | **22 godkända, 6 befintliga projektexkluderingar, 0 fel**, totalt 28 |

67 nya backendfall och nio UI-fall jämfört med `c67f1d1`:s 804/6 respektive 162. Före implementation kördes 40 nya kärnregressioner mot den gamla koden: 37 reproducerade fel och tre passerade. En mellanliggande HA-körning fångade två inkonsekventa testtidsstämplar och en oavsiktligt liberalare boolesk whitespace-tolkning. Fixturerna rättades till verklighetstrogna tider och den tidigare booleska tolkningen bevarades innan helsviterna blev gröna.

Backendkommandon från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /p:VSTestTestCaseFilter=FullyQualifiedName~HomeAssistantSensorValidationTests /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /p:VSTestTestCaseFilter=FullyQualifiedName~HomeAssistant /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontendkommandona i tabellen kördes från `frontend`. SDK 10.0.111 användes uttryckligen eftersom den är installerad lokalt; repository, CI och Docker är fortsatt låsta till 10.0.400. Ingen ny restore, beroendeändring, CI-körning, publicering, containerbyggnad eller full applikationsstart ingick. `git diff --check` kontrollerades under arbetet; normala LF/CRLF-notiser ändrar inte testresultaten.

De sex backendundantagen är oförändrat två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. De nya insamlingstesterna använder EF InMemory, syntetiska konton och direkta anrop till collectorn utan nätverks- eller styrklienter. Det nya browserflödet är helt mockat, gör **noll mutationer** och verifierar sensorfel, återkallat godkännande, tangentbordsåterhämtning och mobilens knappar/överströmning. JSDOMs befintliga undantag för färgkontrast är kvar; full skärmläsar-/kontrastacceptans återstår. Testbilderna är ignorerade lokala artefakter, inte produktionsacceptans.

## Produktion – separat read-only-evidens

Kontrollerat **2026-08-31 omkring 10:07 CEST / 08:07 UTC**:

- `daikin-prisstyrning-1`, PostgreSQL och EMHASS kör med noll omstarter och oförändrade image-referenser enligt [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session`: HTTP 200. Sessionen var oautentiserad; endast boolesk CSRF-utgivning återgavs, inga token-/cookievärden.
- Anonym `/api/thermal/status`, `/api/home-assistant/status` och `/api/home-assistant/entities`: HTTP 401.
- Explicit `BEGIN TRANSACTION READ ONLY` bekräftade en thermal-konfiguration i `Legacy/Legacy` och **noll termiska styrkommandon**.
- Inga nya `apply OK`/`Applied`/`Apply failed`-markörer mellan **05:19:28 och 08:07:44 UTC**. Tidigare accepterad ordinarie skrivning 01:35 CEST är redan dokumenterad. Ingen extra schemakörning eller testsändning gjordes.
- Ingen container, driftinställning, behörighet, credential, kontoägare eller aktiveringsspärr ändrades. Loggkontrollen återgav bara tidsstämplar/resultatmarkörer, inte råloggar.

## Nästa avgränsning och kvarvarande acceptans

1. Granska den samlade lokala serien och kör tillämplig CI inför eventuell motiverad felrättningsrelease genom **samma** Dockhand-stack med befintlig rollbackväg. Den tidigare adminraderingsspärren är ännu inte driftsatt; använd inte den gamla raderingsfunktionen. Gör inte en deploy bara för att en automation körs.
2. Fortsätt datavalideringen vid modellträning och övriga beredskapsgrindar, särskilt redan sparade äldre värden, modellmåttens format/ändlighet, prognosluckor samt hur verkliga uppvärmningsdygn skiljs från importerade/felaktiga rader. Här stärktes livekraven och 21-dagarstelemetritäckningen, inte hela acceptansmodellen.
3. Granska revisionsbindning för en hel fler-entity-historikimport, samtidiga imports/liveinsamling och kontinuitet över omstarter. Processlokal sensorhälsa och inställningslås är inte en flerinstanslösning. Rådata med saknad `last_updated` avvisas avsiktligt; verklig HA-/Recorder-kompatibilitet måste verifieras innan modellträningens täckning bedöms.
4. Verklig kontoägd HA-konfiguration, inloggad UI-acceptans, PostgreSQL-/nätverksprov och husets Shadow-, modell-, grundkurve-, DHW- och hygienkrav återstår. Kodtesterna bevisar inte en fysisk DHW-cykel eller att aktiv styrning kan godkännas.

Huvudplanen, produktionsrapporten och föregående HA-rapport uppdateras med denna avgränsade leverans. Gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument lämnas oförändrade på grund av Kontrollerad mappåtkomst; befintlig förberedd patch finns kvar. Skyddet har inte ändrats eller kringgåtts. Meningsfullt godkänt arbete återstår, så den timvisa fortsättningen har inte avslutats eller ändrats.
