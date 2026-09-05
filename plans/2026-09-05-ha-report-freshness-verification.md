# HA-färskhet: rapportering och ändring är olika saker

## Status

Lokalt implementerad och verifierad första del på `codex/ha-report-freshness`, från master `469e9930340d453b6969b683d6cb84cd290e4b36`. Detta är **inte en färdig eller driftsatt lösning för alla givarvarianter**. Ingen push, PR, imagepublicering, driftsättning, produktionsavläsning eller testsändning ingick i detta pass. Ingen kontokonfiguration, credential, driftläge eller writer ändrades.

Användaren har klargjort att rumsgivarna kommer från flera integrationer. Den färdiga lösningen ska därför vara integrationsoberoende och konfigurerbar per givare. Ingen viss Zigbee-/ESPHome-integration får hårdkodas som förutsättning.

## Implementerat

- `last_reported` läses separat från `last_updated`, med samma strikta UTC-/offsetvalidering. Saknat fält använder den tidigare konservativa uppdateringstiden. Ett närvarande men felaktigt fält blir ogiltigt, inte tyst godkänt.
- Oförändrade värden kan godtas när HA-integrationen har rapporterat dem nyligen. Ett nytt HTTP-svar ensamt föryngrar inte en gammal rapport. Framtida/motsägelsefulla tider och gammal lokal mottagning underkänns fortsatt.
- WebSocket-sessionen kompletteras med ett skrivskyddat REST-anrop varje minut, med 30 sekunders timeout. Samma upplösta konto-/anslutningsrevision används. Fel avslutar live-sessionen och återanslutning kräver en ny startbild.
- Periodisk publicering är atomisk och revisionsbunden. Händelser under hämtningen och borttagningsmarkörer bevaras; nyare rapporter får inte ersättas av äldre köade händelser. Saknade entities tas bort vid fullständig hämtning.
- Sensorns återhämtning kräver tre distinkta rapporteringstider, inte tre HTTP-läsningar av samma cachade rapport. Senast giltiga tid och förändringshastighet använder källans rapporteringstid när den finns, aldrig HTTP-mottagningstid.
- Historik bedöms fortsatt med historiska uppdateringstider. Senare live-rapportering och importens mottagningstid gör inte historiska värden färska.
- Entity-katalogen använder samma tidskontroll som insamlingen. API och svensk UI visar rapporteringstid separat från ändring och mottagning. Serverns förklaring för ett oförändrat men nyligen rapporterat värde visas i den tillgängliga statusen.
- Inga ändringar i legacy-algoritm, ONECTA-payload, styrklient eller databasmodell/migrationer.

## Lokal verifiering

Slutresultat: **1 218 godkända backendtester, 7 överhoppade; 237 godkända UI-tester; 26 godkända browserflöden, 6 befintliga projektexkluderingar**.

Körda kommandon (repositoryrot om inte annat anges):

```text
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll" Prisstyrning.sln /t:Build /p:Configuration=Release /verbosity:minimal
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\vstest.console.dll" Prisstyrning.Tests\bin\Release\net10.0\Prisstyrning.Tests.dll /TestCaseFilter:"FullyQualifiedName~HomeAssistant" /Logger:"console;verbosity=minimal"
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\vstest.console.dll" Prisstyrning.Tests\bin\Release\net10.0\Prisstyrning.Tests.dll /Logger:"console;verbosity=minimal"
cd frontend
npm test -- --run
npm run test:e2e
```

Release-byggnad samt TypeScript/Vite-byggnad passerade. Lokal SDK är 10.0.111; repository/CI ligger kvar på 10.0.400. Ingen SDK-pin ändrades. Verifieringen ovan är därför inte bevis för aktuell CI eller publicerad image.

Första avgränsade HA-körningen gav fyra fel i gamla katalogtestdata där `last_changed` låg efter `last_updated`. Testdata rättades till konsekventa tider; den gemensamma tidskontrollen försvagades inte. De slutliga fullständiga sviterna ovan passerade. PostgreSQL-acceptansen är ett av de sju lokalt överhoppade testen; ingen produktionsdatabas användes för testerna.

## Kvar före release av hela färskhetsändringen

### Förtydligande efter användarens beslut: varning får inte hindra Shadow

Lokalt implementerat efter första delcommitten: misslyckade `telemetry-fresh` och `telemetry-quality` är `Warning` för målmode Shadow, med `Passed=false` bevarat. API:s `ready`, serverns lägesbyte och UI-guiden tillåter dessa två varningar utan att göra data godkända. UI visar begränsningen både i checklistan och vid bekräftelsen. HA-konfiguration, anslutning och mappningskrav kvarstår; andra varningar ger ingen generell förbikoppling. LwtActive och FullActive fortsätter blockeras av varje underkänt krav oavsett varningsetikett. Insamling, modellträning och aktiv skrivvalidering har inte försvagats; Shadow kan vara aktiverat även när tillräckligt giltigt underlag för beräkningar saknas.

Detta ändrar releaseomfattningen: inställningsbar livsteckenskälla och åldersgräns nedan är återstående utveckling, inte ett skäl att hålla tillbaka en avgränsad Legacy/Shadow-release efter normala releasekontroller. Varningar kräver inte att användaren åtgärdar alla givare före Shadow.

Verifierat efter denna ändring: Release-byggnad och full backendkörning med samma MSBuild/vstest-kommandon ovan: **1 227 godkända, 7 överhoppade**. `npm test -- --run src/components/thermal/ModeWizard.test.tsx`: **13 godkända**. `npm run build`: godkänd TypeScript/Vite. `git diff --check`: inga whitespacefel. Full UI-/browser-svit är inte omkörd efter just denna ändring; tidigare totalsiffror ovan gäller föregående del. Ingen produktionsavläsning, driftsättning eller faktisk aktivering ingick.

### Återstående generell givarpolicy

Uppföljande lokal verifiering 2026-09-05: `npm test -- --run` gav **239 godkända UI-tester**. `npm run test:e2e` byggde TypeScript/Vite och gav **28 godkända browserflöden, 6 befintliga projektexkluderingar**. Det nya `shadow-warnings.spec.ts` kontrollerar på både desktop och 320 px mobil att varningen syns i checklistan och slutbekräftelsen, att dialogen ryms och att endast en uttryckligt bekräftad Shadow-begäran skickas till ett simulerat API. Befintliga LWT-spärrtester passerade också. Backend ändrades inte i detta verifieringspass; senaste fulla backendresultat är 1 227/7 ovan. Ingen extern publicering, produktionskontroll, driftsättning eller faktisk lägesändring gjordes.

1. Lägg till kontoägda inställningar per givare för maximal rapportålder och val av färskhetskälla. Behåll separat gräns för vår egen kommunikation/insamling; förläng inte den när en långsam rumsgivare får längre rapportintervall.
2. Stöd en explicit konfigurerad timestamp-entity eller ett `last_seen`-attribut, utan leverantörsberoende. Dokumentera vilka tidsformat som stöds. Saknad, felaktig, gammal eller framtida tid får inte ge ett positivt livstecken. Bind signalen till rätt givare och konto.
3. En allmän HA-ping eller ett kvarliggande `available`-värde är inte bevis för en fysisk givare. Om endast tillgänglighet stöds krävs ett uttryckligt, verifierbart rapporterings-/timeoutkontrakt. Tysta givare utan sådant bevis ska visas som osäkra, inte automatiskt gröna.
4. Visa vald källa, rapportintervall och konsekvens i inställnings-UI med dirty-state, validering och förhandsvisning. Katalog, insamling, historik, modellproveniens och lagrad kvalitetsinformation måste använda samma policy.
5. Testa blandade givare, konto-/policybyte, tappat livstecken, återhämtning och kritiskt rums 30-minuters reservvärde. Säkerhetssignaler som flöde/DHW/avfrostning får inte ärva en lång rumsgivargräns. Kontrollera att hygientestets två femminutersmätningar inte kan uppfyllas av en enda kvarliggande temperaturavläsning med orelaterade livstecken.
6. För varje avgränsad release: full CI/review, verifierad signerad image, färsk rollbackbackup och uppgradering av befintlig `daikin`-stack via Dockhand. Ändra inte driftläge som del av deploy; lämna LWT och FullActive avstängt och DHW-writer i Legacy. Shadow blir valbart med datavarningar, men aktiveras inte automatiskt. Skilj runtime-/inloggningskontroll från accepterad legacy-skrivning och fysisk DHW-verifiering.

Detta dokument är ett återupptagningsunderlag, inte ett aktiveringsgodkännande eller ett påstående att `last_reported` bevisar den fysiska mätningens aktualitet för varje integration.
