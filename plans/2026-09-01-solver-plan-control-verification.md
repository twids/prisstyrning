# Solver-, plan- och aktiv kontrollvalidering – 2026-09-01

## Resultat och avgränsning

Fortsättningen utgår från lokala committen `a6f8050` på `codex/session-recovery-regressions`. Solverresultat, sparade planer och den aktiva LWT-konsumenten valideras nu fail-closed över hela den lokalt testade kedjan. En Shadow-plan, en återkallad modell, ett trasigt plansteg eller overifierad rum-/DHW-/avfrostningstelemetri kan inte användas som vanlig aktiv LWT-indata.

Detta är **lokal källkodsverifiering, inte publicering, driftsättning eller aktiveringsgodkännande**. Ingen full applikation startades, inga migrationer kördes och inga containrar, konton, credentials, driftlägen, scheman eller Daikin-/HA-värden ändrades. Legacyjobben och ONECTA-payloaden är orörda. Produktionens senast dokumenterade läge är fortsatt tidigare evidens: `ControlMode=Legacy`, `DhwWriter=Legacy` och LWT/FullActive avstängt.

## Solverresultat och tidsaxel

- Optimeringsrequesten måste ha en hel och entydig horisont, lika långa prognosarrayer, ändliga fysiskt rimliga värden, giltiga komfortgränser samt en DHW-reservation helt inom horisonten.
- Resultatet måste innehålla exakt ett ordnat steg per requeststeg. Saknade, dubbla eller omkastade index, negativ/överstor/icke-ändlig effekt, saknad temperatur, komfortbrott, ogiltig kostnad, samtidig husvärme under reserverad DHW-kapacitet och solveröverdrag över 45 sekunder avvisas.
- Objektivkostnaden räknas om från effekt, tidssteg och kontots prisprognos. Ett resultat vars totalsumma inte stämmer inom en liten avrundningstolerans avvisas.
- EMHASS-CSV:ns första kolumn måste innehålla zonangivna tidsstämplar. Exakt start och sammanhängande 15-minuterssteg verifieras; motsvarande tidszonoffset accepteras men lokala tider utan offset avvisas.
- Lokal start-/modellmetadata följer den persistenta kön men skickas inte i EMHASS runtime-payloaden. Kända verifieringsfel behåller en typad, sanerad orsak genom kön.
- Ett komfortbrytande solverförslag skapar ingen giltig plan och registreras som `SimulatedComfortBreach`/`ActionRequired`, så Shadow-readiness inte kan tolka det som godkänt.

## Sparad plan och skrivgräns

- Den aktiva konsumenten väljer endast det inloggade kontots senaste aktuella plan. Om den planen är Shadow eller inte exakt `Valid` sker ingen återgång till en äldre plan.
- Planmetadata, modellunderlag, konfigurationsfingeravtryck, full 15-minuterstäckning, varje stegs intervall, effekt, LWT-gräns, DHW-status, temperaturprognos, konfidens och begripliga beslutsorsak verifieras före användning.
- En kryptografisk innehållsfingerprint omfattar plan och samtliga steg. Direkt före en icke-fallbackskrivning läses samma kontoägda plan och aktuell modell-/konfigurationsrevision igen. Ändring, återkallelse eller att det först lästa steget hunnit löpa ut ger säker återgång till noll.
- I LwtActive/FullActive presenterar status-API:t inte en aktuell Shadow/ogiltig plan som nästa styrunderlag. Legacy/Shadow behåller däremot senaste Shadow-plan för jämförelse i UI.
- Regulatorn avvisar även icke-ändliga eller orimliga interna säkerhetsvärden. Ett okänt nuvärde begär uttryckligen noll; om värdet redan är verifierat noll loggas fallback utan onödig skrivning.

## Telemetri och readiness vid aktiv LWT

- Aktiv kontroll kräver en aktuell live-snapshot som efter senaste konfigurationsrevisionen har giltigt flöde samt entydig DHW- och avfrostningsstatus. Saknad mapping, null, stale, import eller felmarkerad signal ger säker nollställning i stället för att tolkas som `false`.
- Representativ rumskorrigering använder endast rum vars sparade kvalitetsbedömning är `Valid` och inte exkluderad. Ett lagrat reservvärde från en trasig kritisk givare kan därför inte ensam utlösa maximal värme eller påstås vara faktisk temperatur.
- Om den kritiska givaren är exkluderad men ett annat viktat rum är giltigt används husets representativa fel utan kritisk-kall-flagga. Saknas varje giltigt viktat rum går aktiv kontroll till noll.
- Readiness-guiden visar ett separat krav, `lwt-safety-inputs`, endast för LwtActive/FullActive. Shadow och Legacy behåller sin tidigare generella telemetrisemantik.

## UX/UI

- Den befintliga lägesguiden visar det nya aktiva säkerhetskravet med konkret åtgärd: mappa flöde, DHW-status och avfrostning och invänta giltig liveinsamling.
- Befintlig statusrad visar `Plan saknas` när aktivt underlag avvisas, och fallbackorsaken visas som text. Shadow-planer är fortsatt synliga i Shadow-vyn men märks inte som aktiva kontrollindata.
- Komfortbrott använder den redan svenska kategorin `SimulatedComfortBreach` i händelsevyn och blockeras som åtgärdskrävande Shadow-evidens.
- Inga frontendkontrakt eller frontendkällfiler ändrades. Den generiska readiness-renderingen, status-/händelsetyperna och responsiva flödena kördes ändå i full UI-/browserverifiering.

## Verifiering

| Kontroll | Slutresultat |
|---|---|
| Första röda solverregressionen | **13/13 röda** före valideraren; samtliga tidigare feltyper reproducerades |
| Samlad fokuserad backendsvit efter sista ändring | **214 godkända, 0 fel** |
| .NET 10 Release med `ContinuousIntegrationBuild=true` | Godkänt, inga rapporterade varningar/fel |
| Hela backendens Release-svit | **1 101 godkända, 6 befintliga undantag, 0 fel** |
| `npm.cmd test` | **207/207**, 17 testfiler |
| TypeScript/Vite-produktionbygge | Godkänt |
| `npm.cmd run test:e2e` | **26 godkända, 6 befintliga projektexkluderingar, 0 fel** |
| `git diff --check` | Godkänt |

En mellanliggande sensor/readiness-körning gav tre avsiktligt synliga fel: ett felaktigt viktat testförväntat och ett för brett krav på DHW/avfrostning även i generell Shadow-telemetri. Kravet avgränsades därefter till aktiva LWT-lägen och 130/130 fokuserade fall passerade. En senare samlad körning fångade tre felaktiga `ShouldWrite`-förväntningar när avvikelsen redan var noll samt en osparad InMemory-fixture; efter testkorrigering passerade 213/213. Den slutliga regressionen för extra CSV-rader utanför requesthorisonten höjde den samlade gruppen till 214/214 och hela sviten till 1 101/6. De mellanliggande felen var test-/avgränsningsfel, inte en ändring av Legacy.

Backendkommandon kördes med installerad SDK **10.0.111** via MSBuild 18.0.11; repository, CI och Docker är fortsatt låsta till **10.0.400**. Ingen restore, paketinstallation, appstart, Docker-build, push, PR, CI-dispatch eller nätverks-/produktionskontroll gjordes. De sex backendundantagen är samma två BatchRunner-, tre ScheduleHistory- och ett live-Nordpool-test. Browserns sex undantag är samma projektavgränsningar; den befintliga icke-fatala `NO_COLOR`/`FORCE_COLOR`-varningen kvarstår.

## Kvarvarande risker och nästa avgränsning

1. Databasvalideringen minskar fönstret före HA-anropet, men kan inte göra ett externt fysiskt serviceanrop atomiskt med PostgreSQL. Telemetri/cache eller konfiguration kan ändras efter sista kontrollinstruktionen. Detta måste bedömas i verklig Shadow och med PostgreSQL-/HA-felinsprutning.
2. Planeringskedjan behöver fortsatt bindning till full livekvalitet för alla pris-, väder-, brine-, effekt-, fas- och rumskällor. De nya gränserna validerar värden och aktiv kontroll, men historisk träningsproveniens och alla sensorers skapande revision är inte bevisade.
3. EF InMemory bevisar kontrakt och kontoavgränsning men inte PostgreSQL-transaktioner, jsonb, concurrency eller flera appinstanser. Delad EMHASS-resultatfil och horisontell skalning är fortfarande single-instance-antaganden.
4. Effekttariff är fortsatt avstängd i produktion. Den separata kapacitetskostnaden ingår ännu inte i den omräknade solverobjektivverifieringen och får inte driftgodkännas utifrån dessa tester.
5. Verklig kontoägd HA-konfiguration, inloggad produktions-UX, representativ träning, minst 21 Shadow-dygn, grundkurveprov, DHW-/hygiencykler och fyra veckors normaliserad jämförelse återstår. LWT/FullActive ska inte aktiveras från denna lokala evidens.

Nästa säkra kodavgränsning är full proveniens/validering av planeringsindata och PostgreSQL-konkurrensprov. En framtida release kräver separat uttryckligt godkännande, tillämplig CI och samma reversibla Dockhand-stack. Legacy/Legacy ska behållas vid en sådan release tills den stegvisa acceptansen uttryckligen är genomförd.
