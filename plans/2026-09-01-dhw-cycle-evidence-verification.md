# Verifiering av DHW-cykelprovenans 2026-09-01

## Resultat

Varje nytt solverjobb och varje sparad värmeplan binds nu till det exakta öppna DHW-läget för det serververifierade kontot. Evidensen innehåller ID för den reserverade cykeln när en sådan redan finns samt antal och SHA-256-fingeravtryck för samtliga öppna kontoägda DHW-rader. Även ett tomt cykelläge fingeravtrycks.

En cykel som skapas, ändras, avslutas, tas bort eller ersätts medan EMHASS arbetar gör därför resultatet ogiltigt. Samma kontroll görs när en aktiv plan läses och omedelbart före en möjlig LWT-skrivning. Andra kontons cykler påverkar inte evidensen.

Ingen driftsättning, migration, kontoändring, HA-skrivning eller Daikin-skrivning gjordes. Produktionens tidigare verifierade `ControlMode=Legacy` och `DhwWriter=Legacy` är oförändrade; LWT och FullActive är fortsatt avstängda.

## Implementation

- Alla muterbara planerings- och livscykelfält i en öppen `DhwCycle` ingår i fingeravtrycket: identitet, typ, källa, status, planerad/accepterad/verklig tid, mål, temperaturer, varaktigheter, kostnader, elpatron, effektprofil, verifieringsräknare och uppskattad sluttid.
- En ny kandidat har före solveranropet en verifierad tom eller befintlig cykelmängd. Om en konkurrerande livscykelobservation skapar en rad under beräkningen avvisas solverresultatet.
- En befintlig pågående eller låst cykel identifieras uttryckligen med rad-ID och måste fortfarande finnas i den öppna, kontoägda mängden.
- Efter godkänt solverresultat sparas/uppdateras DHW-reservationen först, ett nytt exakt fingeravtryck skapas och detta lagras i planens `inputEvidence`.
- På relationsdatabaser sker sista evidenskontrollen, DHW-upsert och planpersistensen i en kort transaktion med `Serializable` isolation. Ett konfliktfel får avbryta planeringen i stället för att skapa en plan från blandade revisioner.
- Äldre planer och köjobb utan DHW-evidens avvisas fail-closed. Evidensen är lokal metadata och skickas inte i EMHASS runtime-payload.
- Legacy-koden nås fortfarande inte av coordinatorn när driftläget är `Legacy`; ONECTA-writer och befintligt legacy-schema ändrades inte.

## Regressioner

- Ny öppen DHW-cykel medan coordinator-solvern arbetar avvisar resultatet och skapar ingen plan.
- Ändrad cykel medan en persistent EMHASS-köpost körs avvisas före completion.
- Saknad DHW-evidens avvisas både i kön och av aktiv planförbrukning.
- Ändrad cykel efter första planläsningen avvisas vid den sista skrivgränsen.
- En verifierad löpande cykel behåller samma reserverade rad-ID genom request och sparad plan.
- En ny reservation får sitt verkliga databas-ID och ett icke-tomt fingeravtryck i den sparade planen.
- En annan kontos cykel påverkar inte ett verifierat jobb.
- DHW-fingeravtrycket förekommer inte i EMHASS-payloaden.

## Kontroller

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release-build | Godkänd utan varningar eller fel |
| Fokuserad coordinator/EMHASS/planförbrukningssvit | 122 godkända, 0 misslyckade |
| Full backendsvit | 1 137 godkända, 6 befintliga undantag, 0 misslyckade |
| `git diff --check` | Godkänd före dokumentationscommit |

Den första fokuserade körningen gav 121 godkända och ett testfel eftersom felinjektionen använde fel skiftläge för den inbäddade JSON-egenskapen och därför inte tog bort evidensen. Testet rättades till det verkliga kontraktet; den fullständiga omkörningen gav 122/122. Detta var ett testfel, inte ett produktfel.

Ett lösningsbaserat `dotnet format` kunde inte starta sin interna build-host eftersom `global.json` kräver SDK 10.0.400 medan den installerade lokala SDK:n är 10.0.111. Inga filer ändrades av det försöket. Mappbaserad whitespace-formattering av endast coordinator-filen lyckades. Bygg och tester kördes med explicit SDK 10.0.111; repo, CI och Docker är fortsatt låsta till 10.0.400.

Frontendkällan ändrades inte i denna leverans. Föregående verifierade resultat, 216 Vitest-tester och 26 tillämpliga Playwright-flöden med 6 projektdubbletter överhoppade, har därför inte körts om och ska inte räknas som nya kontroller här.

## Kvarvarande gränser

- InMemory-regressionerna bevisar kontraktet men inte PostgreSQLs verkliga serialiseringskonflikter. En driftlik integration med två samtidiga DbContext/transactions behövs.
- Historiska telemetri-, väder-, COP- och DHW-profilrader som producerar modeller och empiriska varaktigheter behöver fortfarande full revisionsprovenans.
- Den sista databaskontrollen kan inte vara atomisk med en fysisk HA/P1P2- eller ONECTA-skrivning. Befintlig lease, bekräftelse och safe-zero minskar men eliminerar inte det externa intervallet.
- Verklig HA/EMHASS/prognos/pris måste fortsatt observeras i Shadow enligt de tidigare readinesskraven. Den här leveransen är kodverifiering, inte aktivt driftgodkännande.
