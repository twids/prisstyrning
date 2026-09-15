# Försiktig start och inlärning under drift

## Beslut och gränser

Godkänt av användaren 2026-09-15: planera och implementera en lösning som kan
börja reglera efter säker driftsättningskontroll, utan ett kalenderkrav på 21 dygn.
Tre veckor är en första utvärderingspunkt, inte en utfästelse om optimal effekt.
Litet värmebehov ger begränsad information även efter många kalenderdagar.

Den här implementationen aktiverar inte LWT eller FullActive i produktion.
Shadow och Legacy-DHW ska fortsätta oförändrat. Att få fram en preliminär plan
innebär inte att den planen får skickas till värmepumpen.

## Arkitektur

Två separata vägar under LwtActive:

1. **Försiktig temperaturreglering:** Daikins fungerande väderkurva plus långsam
   PI-återkoppling från rum, högst ±1 °C och ett reglagesteg per 30 minuter.
   Behöver varken EMHASS, COP-modell, prisprognos eller tränad husmodell.
   Inget prisberoende förvärmande eller avsiktligt underskriden komfort.
2. **Modellbaserad prisoptimering:** tillåts först med validerade modeller,
   aktuella prognoser och en konsistent plan. Evidens, inte antal kalenderdagar,
   avgör när den får användas. Förlorad modelevidens tar bort prisbidraget,
   inte den redan verifierade försiktiga temperaturregleringen.

Säkerhetsfel i sensorer, kommunikation eller styrning är något annat än en
omogen modell: då ska avvikelsen återgå till noll och larmas. En misslyckad
nollställning måste visas som misslyckad, inte som verifierad grundkurvedrift.
Daikins grundkurva måste kunna fungera utan applikationen. Förlorad kommunikation
kan göra det omöjligt att nollställa; watchdog/återställning måste verifieras på
den verkliga anläggningen och får inte påstås fungera bara för att kod finns.

## Leveranssteg och acceptans

### A. Reglerkärna (implementerad och lokalt verifierad)

- Separat, deterministisk regulator utan modell- eller prisberoende.
- Faktisk tid mellan utvärderingar, inte tid sedan senaste skrivning, styr PI.
- Ingen integrering under DHW, avfrostning, otillräckligt flöde eller säkerhetsfel.
- Anti-windup vid mättnad, dödband, begränsning av ändringstakt och ±1 °C.
- Kritiskt kallt rum får aldrig orsaka en negativ begärd avvikelse.
- Tester för jämna värden, omstart, långa avbrott, återupprepade utvärderingar,
  mättnad, komfort, reglagesteg och säkerhetsfel.
- Kärnan är kopplad till en separat, serververifierad inkopplingsväg enligt B.

### B. Inkoppling och säkerhetsbevis (implementerat; lokala simulerade tester godkända)

- Additiv, kontobunden lagring av vald strategi och regulatorns senaste
  utvärderingstid. Standard/migration får inte aktivera någon installation.
- Kort guidad driftsättningskontroll: manuellt bekräftat LWT/väderkurveläge
  (inte RT), grundkurva, rimliga rumsvärden, driftstatus, actuator och feedback.
- Explicit godkänt begränsat skrivtest, observerad återkoppling samt verifierad
  nollställning. Ett gammalt lyckat HTTP-anrop eller en kryssruta är inte bevis.
- Bevis knyts till konto och aktuella styr-/sensormappningar; ändring återkallar
  beviset. Inga hemligheter i revisionsloggen.
- Worker använder aktuell observerad avvikelse, lease, kill switch och kontroller
  igen före skrivning. Hantera omstart, manuell ändring och samtidig rollback.
- Integrationsprov med simulerad HA och databas före ändrade aktiveringsspärrar.
- Kontrollera att inlärning fortsätter också under LwtActive, inte bara Shadow.

### C. Readiness och UX/UI (implementerat och lokalt verifierat)

- Separera **Säker att starta** från **Redo för prisoptimering**.
- Modell-/COP-/21-dygnskraven blockerar inte den försiktiga vägen; behåll dem i
  nuvarande aktiveringsväg tills den nya vägen faktiskt är säker och testad.
- Visa tydligt: Skuggar / Försiktig reglering / Validerad prisoptimering,
  modellens osäkerhet och varför ett prisbidrag används eller avstår.
- Graf visar uppmätt LWT, försiktigt förslag och modellförslag åtskilt.
- Modeller bedöms mot undanhållna data och faktisk värmedrift/vädervariation.
  UI får inte visa kalenderbaserad procent mot ”optimal”.
- Ingen automatisk utökning till ±3 °C eller FullActive utan separat godkännande.
- Mobil-, tangentbords-, skärmläsar- och regressionstest av lägesguiden.

### D. Verifiering och release

- Full backend, frontend, UI/e2e samt relevanta databas-/lease-tester.
- Befintliga golden-master-tester för Legacy måste förbli gröna.
- Granskad release till befintlig Dockhand-stack med känd rollback, inte ny stack.
- Separat verifiering av aktivering på den verkliga anläggningen. Programvarans
  tester ersätter inte bevis på verklig write/readback/fallback.

## Nuläge

Utgångspunkt: origin/master 1aee7107cd47129d8f30e6c8b2ccfb84dec84658.
Den gamla aktiveringsvägen kräver fortfarande 21 dygn och validerade modeller.
Denna fil är införandeplan, inte ett intyg om färdig aktiv styrning.

### Verifierat 2026-09-15

- Försiktig PI-reglering behöver ingen tränad modell. Endast validerade aktiva
  planer får ge begränsat prisbidrag; Shadow-planer ger aldrig skrivunderlag.
  Försvunnen modelevidens tar bort prisbidraget och lämnar rumskorrigeringen.
- Kontobunden `ThermalStartupState` och additiv EF-migration är genererade.
  Standard är avstängt. `Thermal:AllowConservativeStart` är explicit false.
- Guidad start kräver aktuella säkerhetskontroller och tre manuella bekräftelser.
  Servern sparar återställningsavsikt och lease före testet 0 / ett positivt
  reglagesteg / 0. Varje steg kräver numerisk återkoppling. Misslyckad nollställning
  visas som återställning krävs, inte som lyckad Shadow-återgång.
- Kontooperationer serialiseras även mellan instanser via PostgreSQL-lås.
  Sensor-/styrkonfiguration och HA-anslutningsrevision binds till inkopplingsbeviset.
  Kontroller upprepas precis före varje icke-nollskrivning.
- UI skiljer säker start från modellmognad, kräver inte 21 dygn för försiktig start
  och visar ett separat skrivfritt nulägesförslag i temperatur/LWT-grafen.
  Inlärningsjobbet fortsätter även i LwtActive. Tre veckor är inte en effektgaranti.
- Full backend: `dotnet test Prisstyrning.Tests/Prisstyrning.Tests.csproj
  --configuration Release --verbosity quiet`: 1578 passerade, 7 överhoppade, 0 fel.
- Frontend: `npm run test -- --run --maxWorkers=2`: 29 filer, 322 tester passerade.
- `npm run test:e2e -- --workers=2`: TypeScript/Vite-bygge godkänt, 54 tester
  passerade och 6 projektspecifika överhoppade. Mobil och tangentbord ingår.
- Bildgranskning hittade en degenererad grafskala vid ett ensamt nulägesförslag.
  Skalan inkluderar nu -1 till +1 °C (utvidgas för större historiska värden), och
  tomlägestexten skiljer modellplan från nulägesförslag. Efter sista ändringen:
  produktionsbygge och fyra riktade Playwright-tester passerade; grafens sex
  komponenttester passerade efter att textförväntan uppdaterats. Desktop- och
  mobilbilder granskade; slutbilden visar den separata simuleringspunkten korrekt.
- `dotnet ef migrations has-pending-model-changes --configuration Release
  --no-build`: inga modelländringar saknar migration. EF-verktyget 10.0.2 varnar
  om att runtime är 10.0.11. Ingen migration har körts mot produktion.
- `git diff --check` och `git diff --cached --check`: inga whitespacefel.
- README uppdaterad med strategi, flaggor, inkoppling och begränsningar.

### Återstår före release/aktivering

- Kör riktiga PostgreSQL-acceptansprovet i CI, inklusive nya samtidighets- och
  låsåterhämtningsfallen. Lokal Docker-engine saknas; InMemory-tester bevisar
  inte PostgreSQL-lås. Kontrollera övriga releasecheckar på exakt commit.
- Granskad commit/release och Dockhand-driftsättning återstår. Ingen push,
  driftsättning, HA-skrivning eller aktivering har gjorts i detta implementationssteg.
- Verifiera verklig RT-till-LWT-omställning, fungerande grundkurva och oberoende
  återställning på anläggningen innan flaggor/guide används. Kodtester ersätter
  inte fysisk write/readback/nollställning. FullActive och ±3 °C är fortsatt separata.
