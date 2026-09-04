# Isolerad PostgreSQL-acceptans för termisk styrning 2026-09-01

## Resultat

En explicit opt-in-harness finns nu för de PostgreSQL-egenskaper som EF InMemory inte kan bevisa. Testkoden kompileras i varje vanligt bygge men rapporteras som överhoppad med en konkret orsak tills harnessen uttryckligen tillhandahåller en isolerad databas. Därmed kan frånvaro av PostgreSQL aldrig redovisas som en falskt godkänd kontroll.

Ingen produktionsdatabas, Unraid-/Dockhand-stack, migration i drift, app, worker, Home Assistant, P1P2MQTT eller ONECTA-klient används. Testet lämnar anläggningen i `ControlMode=Legacy` / `DhwWriter=Legacy` och kräver att inga planer eller styrkommandon har skapats.

## Säker testgräns

`scripts/test-postgres-acceptance.ps1` skapar ett unikt PostgreSQL 17-system i en tillfällig Docker-container:

- slumpmässig port publiceras endast på `127.0.0.1`;
- ingen volym, befintlig databas eller produktionsnetwork monteras;
- databasnamnet börjar alltid med `prisstyrning_acceptance_`;
- containern körs med `--rm` och stoppas i `finally` även när testet misslyckas;
- anslutningen saknar applikations-, HA- och Daikin-hemligheter.

Själva testet kontrollerar på nytt loopback-host, namn-prefix, tomt `public`-schema och PostgreSQL 17 eller senare innan migrationerna körs. Ett befintligt schema gör att testet stoppar utan att radera någonting.

## Acceptanskriterier

Den databasstödda körningen ska:

1. applicera samtliga riktiga EF-migrationer på en tom PostgreSQL 17-databas;
2. skapa 400 dygns femminuterstelemetri för två konton, totalt 230 400 rader;
3. köra `ANALYZE` och kräva att `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` använder `IX_ThermalTelemetrySamples_UserId_TimestampUtc` med ett indexvillkor för konto och tidsfönster;
4. omvalidera både 2R2C och COP fem uppmätta gånger efter uppvärmning och kräva p95 under fem sekunder;
5. ändra en committad historisk rumsmätning i en separat DbContext och kräva att 2R2C blir `Changed`, medan COP förblir `Current` eftersom just rumsfältet inte ingår i COP-underlaget;
6. låta två serializable-transaktioner läsa samma tomma öppna DHW-mängd och försöka infoga var sin cykel; exakt en får committa och den andra måste stoppas av PostgreSQL med SQLSTATE `40001`;
7. verifiera att kontots driftläge fortfarande är Legacy/Legacy och att inga planer eller styrkommandon har skapats.

## Kontroller i detta pass

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release-kompilering av harnessen | Godkänd utan varningar eller fel |
| Sex anslutnings-/säkerhetsregressioner | Godkända; remote host, fel databasnamn och saknad konfiguration stoppas |
| Full backendsvit | 1 180 godkända, 6 befintliga undantag och 1 uttryckligt PostgreSQL-opt-in-undantag, 0 misslyckade |
| Test discovery utan anslutningssträng | Ett tydligt opt-in-undantag, ingen falsk passering |
| PowerShell AST/syntax och felväg utan Docker-engine | Godkänd; stoppar kontrollerat före containerstart |
| Verklig PostgreSQL 17-körning | Blockerad lokalt; inte godkänd |

Docker Desktop 4.55 på arbetsstationen kraschar före engine-start på en gammal intern `dockerInference`-socket som Windows inte kan flytta eller radera medan den ligger i detta felaktiga reparse-tillstånd. Inga containrar hann skapas, ingen Docker-inställning ändrades och de kraschade processerna stoppades. Produktions-PostgreSQL användes avsiktligt inte som genväg. En omstart eller reparation/uppgradering av den lokala Docker-installationen krävs innan harnessen kan ge det saknade databasbeviset.

## Kvarvarande gränser

- Uppföljning 2026-09-04: den lokala `desktop-linux`-kontexten pekar på Docker Desktops namngivna pipe, men pipe/engine är otillgänglig. Inga engine-/containerstarter eller reparationer gjordes och det verkliga databasprovet är fortfarande inte kört. Det är en aktuell tillgänglighetskontroll, inte en upprepad diagnos av kraschen ovan. Se [byggproveniensrapporten](2026-09-04-thermal-build-provenance-verification.md).
- Källomvalideringens faktiska p95, indexplan och PostgreSQLs serializable-konflikt är ännu inte uppmätta; de får inte beskrivas som godkända förrän harnessen passerar.
- Harnessen provar en enskild appinstans mot en isolerad databas. Horisontell skalning, delad solver/resultatfil och instansöverskridande cache-/revisionsbeteende behöver separata tester.
- PostgreSQL kan inte göra intervallet mellan en committad plan och ett fysiskt HA/P1P2-/ONECTA-anrop atomiskt. Writer-lease, slutlig evidenskontroll, safe-zero och återkopplingsverifiering är fortsatt nödvändiga.
- Verklig kontoägd HA-historik, Shadow, representativt väder, grundkurva, komfort och DHW-/hygiencykler återstår. Detta är en lokal testleverans, inte ett aktiverings- eller driftsättningsgodkännande.
