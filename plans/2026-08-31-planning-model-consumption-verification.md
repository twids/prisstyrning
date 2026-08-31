# Modellunderlag vid planering och säker köhantering – 2026-08-31

## Resultat och avgränsning

Fortsättningen utgår från `8caf1c5` på `codex/session-recovery-regressions`. Koordinatorn och EMHASS-arbetaren använder nu samma modellbedömning som träning, beredskap och modell-API. Lokala regressionstester visar att underkända eller återkallade modeller inte längre ersätts av gissade parametrar och sparas som en ny giltig plan.

Detta är **lokal källkod, inte publicering, driftsättning eller aktiveringsgodkännande**. Legacyalgoritm, återkommande DHW-jobb, ONECTA-payloads, regulator, writer-handover, databasmodell och migrationer är oförändrade. Inga produktionskonton, credentials, driftlägen eller containrar ändrades. Ingen extra schemakörning eller testsändning gjordes.

## Modellkonsumenter

- Både aktiv 2R2C- och COP-version måste tillhöra kontot och klara `ThermalModelEvidence.Assess`. Saknad modell, äldre ofullständiga mått, ogiltig JSON, underkänd validering, felaktiga perioder eller framtida modeller stoppar planeringen före solveranrop. Modeller med samma skapandetid väljs entydigt med versions-ID.
- Husmodellen läses med samma JSON-inställningar som träningsjobbet skriver. Den tidigare skiftlägeskänsliga läsningen kunde skapa nollvärdesparametrar ur korrekt camelCase-JSON och ge NaN i värmeparametrarna.
- Verifierat effekttecken krävs innan kostnadsoptimering. Köldbärartemperatur, LWT och värmeeffekt för COP-prediktion får inte vara saknade eller icke-ändliga; inga gissade reservvärden används för dessa indata. Fullständig validering av alla planeringsindata är däremot ett separat återstående steg.
- Ett kontoavgränsat fingeravtryck följer requesten genom den befintliga persistenta kön och sparas additivt i planens input-JSON. Det binder versionernas innehåll, anläggning, rum, entity-mappning och HA-anslutningens revisionsmetadata samt driftläge och ursprunglig telemetritid. Kontot och båda versions-ID:n måste fortfarande stämma. Fingeravtrycket är jämförelsemetadata, inte en kryptografisk behörighet eller ersättning för kontoåtkomst.
- HA-frågan projicerar endast revision och aktiveringsflaggor. Varken URL, anslutningsobjekt eller krypterade/avkrypterade credentials serialiseras i underlaget. EMHASS får inte dessa lokala metadata; test verifierar att runtime-payloaden är exakt densamma med och utan dem.
- Koordinatorn kontrollerar underlaget före köläggning och efter resultatet. Arbetaren kontrollerar det före och efter solveranrop. Återgång till Legacy, modellåterkallelse, ändrade mått/inställningar/rum/entities/HA-revision eller telemetri äldre än tio minuter stoppar resultatet vid dessa gränser. Ett annat kontos ändringar påverkar inte beräkningen.
- Legacy och avstängd EMHASS gör ingen ny planering. Shadow får fortsatt samla träningsdata utan färdig COP-modell eller verifierat effekttecken, men kan inte skapa kostnadsoptimerade planer förrän underlaget är klart. En ny godkänd modellversion kan återuppta planering utan lägesbyte eller ändrad DHW-writer.
- `power-sign` och `cop-model` krävs nu redan vid beredskap för LwtActive, inte först vid FullActive. Detta samordnar aktiveringskraven med den faktiska kostnadsplaneringen; det aktiverar ingenting.

## Persistenta kön

- Ett väntande anrop kontrollerar hela sin request innan ett resultat godtas. Om nyare indata ersatt ett väntande jobb får den gamla anroparen inte den nya beräkningens resultat. Den nya anroparen kan fortfarande få sitt resultat.
- Jämförelsen är semantisk JSON-jämförelse, inklusive ordnade prognosarrayer. `RequestJson` är `jsonb` i PostgreSQL; ändrad indragning/nyckelordning är inte ändrade indata. Ett extra regressionstest fångade den första implementationens alltför strikta strängjämförelse.
- Slutförande och felmarkering kontrollerar konto, ägare, försök, request, löptid och befintlig concurrency-stamp. Ett redan utgånget försök kan varken slutföra eller felmarkera en ny claim, även om samma worker återtagit den. Ett nytt giltigt försök kan slutföras.
- Äldre körequestar utan modellunderlag avvisas av arbetaren och behöver räknas om. Befintliga tabeller/kolumner, externa API:er och EMHASS-payloads har inte ändrats.

## UX/UI

- Koordinatorns kända modellunderlagsfel får en begriplig svensk orsak i den deduplicerade revisionshändelsen, inte bara ett generiskt planeringsfel. Texten klargör att sista giltiga plans 60-minutersgräns räknas från när planen skapades, inte från varje nytt försök.
- Händelsevyn förklarar att logg och antal är daterad historik, **inte en lista över aktuella larm**. Planeringskategorin och flera andra kategorier har svenska etiketter. Historiska händelser presenteras inte som nya ARIA-larm.
- Varningens textetikett finns även på mobil; färg och ikon är inte enda skillnaden. Revisionsloggen har namngiven sektion, korrekt rubriknivå, semantisk lista och `time`-element i svensk tid. Felaktig tidsstämpel visas som okänd i stället för att krascha vyn.
- Initial laddning, tom lyckad logg och läsfel skiljs åt. Tidigare hämtad historik får ligga kvar vid uppdateringsfel men är uttryckligen märkt. Noll varningar eller tom logg påstås inte när första hämtningen misslyckats.
- **Hämta historik igen** gör endast läsning. Tangentbordsfilter, återhämtning från 503, dold rå serverfeltext, tillgänglighet och mobil layout är testade. Desktop- och 320-pixelsbilder granskades visuellt. Bildernas Shadow/status är syntetiska browserfixturer, inte produktionen.

## Verifiering

| Kontroll | Slutresultat |
|---|---|
| .NET 10 Release med `ContinuousIntegrationBuild=true` | Godkänt, inga rapporterade varningar/fel |
| Hela backendens Release-svit efter sista ändring | **1 029 godkända, 6 befintliga undantag, 0 fel** |
| `npm.cmd test` | **207/207**, 17 testfiler |
| TypeScript/Vite-bygget i `npm.cmd run test:e2e` | Godkänt |
| `npm.cmd run test:e2e` | **26 godkända, 6 befintliga projektexkluderingar, 0 fel** |
| `git diff --check` | Godkänt |

49 backendfall och sju UI-fall har tillkommit jämfört med föregående 980/6 och 200. Ett nytt browserflöde körs på två skärmstorlekar och kontrollerar att inga muterande API-anrop skickas.

Före rättningen misslyckades 15 av 16 nya koordinatorfall och tre av sju köfall; de visade felaktigt godtagna modeller/resultat, NaN från korrekt JSON och bristande koppling mellan request/claim/resultat. De första 23 fallen passerade efter rättning. Det senare JSON-formateringstestet misslyckades i första köfixen medan den senaste anroparens kontrollfall passerade; semantisk jämförelse rättade felet. Därefter passerade 101 fokuserade backendfall och sju UI-fall. En hel mellanliggande backendkörning passerade 1 027/6; efter två ytterligare testvarianter för gammal workers felmarkering och förtydligad händelsetext kördes bygg och hela sviten om till slutresultatet ovan.

Backendkommandon från repositoryroten (ingen restore/full appstart):

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release '/p:VSTestTestCaseFilter=FullyQualifiedName~JointPlanModelConsumptionTests|FullyQualifiedName~EmhassModelConsumptionTests|FullyQualifiedName~ThermalOptimizationQueueTests|FullyQualifiedName~ThermalReadinessEvidenceTests|FullyQualifiedName~EmhassClientTests' /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

De första röda körningarna använde samma VSTest-kommando med filter `JointPlanModelConsumptionTests` respektive `ThermalOptimizationQueueTests`. JSON-formatkontrollen använde `FullyQualifiedName~JsonbFormatting|FullyQualifiedName~ReplacedRequest_LatestCaller`. Den första gröna kontrollen kombinerade koordinator- och kötestklasserna. Frontendkommandon från `frontend`: `npm.cmd test -- ThermalEventsPage`, `npm.cmd test` och `npm.cmd run test:e2e` (inkluderar `npm run build`). Ingen separat beroendeinstallation, audit, push, CI, imagepublicering eller containerbyggnad kördes.

Lokalt används installerad SDK **10.0.111**; repo/CI/Docker är fortsatt låsta till **10.0.400**. Sex backendundantag är oförändrade: två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. Browserns sex undantag är befintliga projektavgränsningar. JSDOMs färgkontrastundantag är kvar. Node rapporterade den befintliga icke-fatala `NO_COLOR`/`FORCE_COLOR`-varningen; alla testkommandon slutfördes.

## Drift och fortsättningsschema

Ingen ny produktionsavläsning ingick i denna kodleverans. Sista dokumenterade läsningen i [produktionsrapporten](2026-08-30-production-verification.md) var 17:35–17:38 CEST: Legacy/Legacy, oförändrade images och inga nya accepterade skrivningar. Det är tidigare evidens, inte en ny hälsokontroll. Den ordinarie 13:35-körningen ska fortsatt inte beskrivas som en ny lyckad skrivning. Produktionens äldre separata misslyckade record ändrades inte.

På användarens begäran kontrollerades den befintliga **Fortsätt prisstyrning**. Den var redan aktiv **varannan timme** i samma tråd; schemat behölls och ingen dubblett skapades. Appens schemavy verifierades. Prompten kräver fortsatt Legacy och uttryckligt godkännande före driftsättning/aktivering. Meningsfullt godkänt implementationsarbete återstår, så automationen behålls.

## Nästa avgränsning och återstående acceptans

1. Granska slutlig plan-/solverresultatvalidering samt regulatorns och DHW-writerns konsumtion av sparade planer. Kontrollerna i denna leverans är före/efter asynkron beräkning, **inte en atomisk databas- eller fysisk skrivbarriär**. En konfigurationsändring efter sista kontrollen och redan sparade planer behöver egen behandling. Komfortbrott, saknade/dubbla/out-of-range steg, tidsaxel, ändliga värden och full horisont måste bedömas i hela kedjan. Planens befintliga 0,85-konfidens är en heuristik, inte ny kalibrerad modellkonfidens.
2. Bind träningsmodeller och historiska telemetrirader till den konfiguration/HA-revision som faktiskt skapade dem. Den nya planeringssnapshoten intygar aktuella modell-/konfigurationsinnehåll, **inte historisk träningsproveniens**. Samtliga planeringsindata, rumsvikter, prognos-/pristäckning, driftfaser och sensorbyte måste granskas; denna ändring tar endast bort modell-/COP-reservvärden.
3. PostgreSQL-exekvering och konkurrensprov återstår. Formatregressionen simulerar jsonb-formatering i EF InMemory; den är inte ett liveprov med PostgreSQL. Delad solver/resultatfil och instansöverskridande serialisering, samtidiga importer samt anslutningsrevisioner behöver egen acceptans innan fler appinstanser tillåts.
4. Verklig kontoägd HA-konfiguration, inloggad produktions-UX, representativ modellträning, Shadow-dygn, grundkurveprov, DHW-/hygiencykler och normaliserad besparingsjämförelse återstår. Aktivera inte LWT/FullActive utifrån de syntetiska testerna. Äldre konton/credentials får inte korrigeras utan verifierat ansvar och mandat.
5. Granska samlad lokal serie och kör tillämplig CI före en separat godkänd release genom samma Dockhand-stack med befintlig rollbackväg. Ingen publicering eller driftsättning ingår här. Använd inte den äldre adminraderingen i produktion; den lokala spärren är ännu inte driftsatt.

Denna rapport, huvudplanen, föregående modellrapports nästa steg samt produktionsrapportens källkods-/schemastatus uppdaterades. Gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument är oförändrade på grund av Kontrollerad mappåtkomst. Den förberedda patchen finns kvar; skyddet har inte ändrats eller kringgåtts.
