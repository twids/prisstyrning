# Verifiering av planeringsunderlag 2026-09-01

## Resultat

Den lokala planeraren binder nu varje EMHASS-begäran och sparad värmeplan till exakt kontoägd femminuterstelemetri och exakt senaste prisunderlag för kontots elområde. Underlaget verifieras före och efter solveranropet, när en aktiv plan läses och omedelbart före en möjlig styrskrivning. Ändrad, borttagen, för gammal eller fel kontoanknuten evidens gör att planeringen avvisas och måste räknas om.

Ingen driftsättning, containerändring, kontoändring, HA-skrivning eller Daikin-skrivning gjordes. Produktionens tidigare verifierade `ControlMode=Legacy` och `DhwWriter=Legacy` påverkas inte av denna källkodsleverans; LWT och FullActive har inte aktiverats.

## Genomförd avgränsning

- Planering kräver exakt en aktiverad mappning för utetemperatur, LWT, RWT, flöde, brine in, tanktemperatur, värmepumpseffekt, fastighetseffekt, DHW-status, avfrostning, elpatron och väderprognos.
- Senaste telemetrin måste höra till kontot, vara högst tio minuter gammal och ha giltig kvalitetsmetadata efter aktuell anläggningsrevision.
- Representativ rumstemperatur tas från den befintliga kvalitetsbedömda reglertelemetrin. Exkluderade eller syntetiska kritiska reservvärden kan därför inte ensamma dra planen mot maximal värme när ett annat giltigt rum finns.
- Flöde, temperaturdifferens och avgiven värme måste vara fysiskt konsistenta. Avfrostning och elpatron måste vara verifierat avstängda för ett ostört planeringsögonblick.
- Aktiv DHW accepteras bara när en pågående kontoägd DHW-cykel är registrerad. Under den fasen används grundkurvans LWT och klimatskalets uppskattade värmebehov som COP-indata för husvärmen, så DHW-temperatur och DHW-last inte förorenar rumsuppvärmningens COP-antagande. Reservationen ligger kvar i den gemensamma planen.
- Pris- och prognos-JSON avvisas vid fel typ, dubbla tidpunkter, ogiltiga tal, felaktiga kvartgränser eller otillräcklig aktuell täckning. Prisperioder behålls som 15 minuter.
- Saknad del av 48-timmarshorisonten uppskattas uttryckligt: pris från motsvarande kvart föregående dygn eller verifierat medel, väder genom att hålla senaste giltiga prognospunkt. Verifierade och uppskattade 15-minuterssteg lagras separat.
- Planens konfidens härleds nu från modellbasen och minsta verifierade pris-/vädertäckning, i stället för ett fast värde. UI visar detta som `Planens konfidens`, visar täckningsgrad och förklarar varje uppskattad svans. Äldre planer utan strukturerad evidens märks som att underlagsdetaljer saknas.
- Det lokala evidensobjektet med telemetri- och prisfingeravtryck skickas aldrig till EMHASS.
- Beslutsrubriken på plansidan har fått korrekt semantisk rubriknivå och de nya underlagsförklaringarna täcks av komponent-, accessibility- och browsertester.

## Kontroller

| Kontroll | Resultat |
| --- | --- |
| Fokuserade backendtester för coordinator, EMHASS, planförbrukning och evidens | 116 godkända, 0 misslyckade |
| .NET 10 Release-build | Godkänd utan varningar eller fel |
| Full backendsvit | 1 131 godkända, 6 befintliga undantag, 0 misslyckade |
| Fokuserade frontendtester | 9 godkända, 0 misslyckade |
| Full Vitest-svit | 216 godkända i 19 filer, 0 misslyckade |
| TypeScript/Vite produktionsbygge | Godkänt |
| Playwright | 26 godkända, 6 avsiktligt överhoppade, 0 misslyckade |

Backend verifierades med lokalt installerad .NET SDK 10.0.111 genom explicit MSBuild/VSTest-anrop eftersom `global.json` och CI/Docker är låsta till 10.0.400. Den första vanliga `dotnet build`-starten stannade därför på SDK-upplösningen; det var inte ett källkodsfel.

## Kvarvarande gränser

- Den exakta DHW-cykelraden och dess revision ingår ännu inte i planens fingeravtryck. Förändringar sammanfaller normalt med ny telemetri, men cykelprovenansen ska bindas uttryckligt innan aktiv gemensam DHW-styrning övervägs.
- Pris- och väderuppskattningarna samt konfidensvikten är säkra, transparenta heuristiker; de är ännu inte kalibrerade mot husets verkliga prognosfel eller kostnadsutfall.
- PostgreSQL-konkurrens, flera appinstanser, EMHASS-resultatfilen och intervallet mellan sista databascheck och fysisk HA/P1P2-skrivning kräver separat driftlik verifiering.
- Verklig HA-/prognos-/prisdata och EMHASS måste köras i Shadow och observeras över tid. Den strikta väntan vid avfrostning, okänd elpatron eller oregistrerad DHW är avsiktligt säker men måste bedömas mot verklig tillgänglighet.
- Effekttariff är fortsatt avstängd i nuvarande anläggning. Den aktiva tariffens objektiv och verkliga total-effektdata behöver egen acceptans innan funktionen används.
- Historiska träningsrader behöver fortfarande full revisionsprovenans. Hygien- och DHW-gaterna samt de ursprungliga shadowkraven gäller oförändrat.

Detta är kodverifiering, inte operativt godkännande. Nästa säkra steg är granskning/CI av den samlade källkodsserien och därefter, först efter uttryckligt godkännande, en Legacy-bevarande uppgradering följd av kontoägd HA-konfiguration och Telemetry/Optimizer Shadow.
