# Verifiering av DHW-profilprovenans 2026-09-01

## Resultat

Varje skattad DHW-profil binds nu till det exakta kontoägda historiska underlag som användes för varaktighet, effektfaser och COP. Källbeviset följer med både solverjobbet och den sparade värmeplanen. Historiken återläses före och efter asynkron optimering, när en aktiv plan hämtas och vid den sista kontrollgränsen före en möjlig LWT-skrivning.

Om en använd cykel eller femminutersmätning ändras, försvinner eller ersätts av nyare kvalificerat underlag avvisas den gamla planen. Även ett uttryckligt fallbackunderlag utan godkänd historik har ett konto- och inmatningsbundet SHA-256-fingeravtryck, så att senare tillkommen giltig historik tvingar fram en ny beräkning.

Ingen driftsättning, migration, kontoändring, HA-skrivning eller Daikin-skrivning gjordes. Legacy-DHW och ONECTA-koden ändrades inte. Produktionens tidigare verifierade `ControlMode=Legacy` och `DhwWriter=Legacy` är oförändrade; LWT och FullActive är fortsatt avstängda.

## Implementation

- Kandidater begränsas till högst 100 av de senaste 200 kontoägda, slutförda cyklerna av samma `Eco`- eller `Comfort`-typ.
- En cykel används bara när start, måluppfyllelse och avslut är verifierade och ordnade, minst två målmätningar finns, temperaturerna ligger nära aktuell start och mål samt uppvärmningstiden är 15–180 minuter.
- Empirisk varaktighet kräver minst tre giltiga cykler. 90-percentilen reserverar fortsatt en konservativ sammanhängande körtid.
- Empiriska effektfaser kräver verifierad Shelly-riktning, exakt en aktiverad mappning för DHW, avfrostning, elpatron och värmepumpseffekt samt minst tre cykler med minst 80 procents femminuterskontinuitet och högst 7,5 minuters luckor.
- Avfrostning, okänd elpatronfas, dubbletter, orimlig effekt och ogiltig kvalitetsmetadata utesluts. Elpatrondrift hålls skild från kompressor-COP; endast verifierade kompressormätningar med COP 1,2–8 används.
- Beviset innehåller normaliserad profiltyp, start/mål, brinetemperatur, antal använda cykler och mätningar, effektteckenstatus, vilka empiriska delar som användes samt ett fingeravtryck över exakt valda rader och relevanta fält.
- En profil från en redan låst/pågående cykels sparade effektprofil binds i stället till exakt reserverat cykel-ID och det befintliga öppna DHW-fingeravtrycket.
- Beviset är lokal orkestratormetadata och skickas inte till EMHASS.
- Äldre planer och köjobb utan profilbevis avvisas fail-closed.

## Regressioner

- Ändrad eller nytillkommen kvalificerad profilhistorik under coordinator- eller kösolverns arbete avvisar resultatet.
- Samma historikändring avvisas när en aktiv plan läses och vid den sista skrivgränsen.
- Saknat profilbevis avvisas av både den persistenta kön och aktiv planförbrukning.
- Ofullständigt verifierade cykler används inte, men fallbackkällan förblir verifierbar och reagerar när cykeln blir giltig.
- Tre kompletta Comfort-cykler lär separata kompressor- och sena elpatronfaser; ändrad använd telemetri återkallar beviset.
- Andra kontons slutförda cykler påverkar inte det verifierade kontots köjobb.
- Profilfingeravtrycket förekommer inte i EMHASS runtime-payload.
- Legacy-writer förblir `Legacy` och inga styrkommandon skapas i regressionerna.

## Kontroller

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release-build | Godkänd utan varningar eller fel |
| Fokuserad DHW-profil/coordinator/EMHASS/planförbrukningssvit | 131 godkända, 0 misslyckade |
| Full backendsvit | 1 144 godkända, 6 befintliga undantag, 0 misslyckade |
| Mappbaserad whitespace-formattering av nio ändrade C#-filer | Godkänd |
| `git diff --check` | Godkänd före dokumentationscommit |

Den första fokuserade körningen efter implementationen gav 127 godkända och fyra testfel eftersom de nya historikraderna inte motsvarade den profiltyp som hygienplaneraren faktiskt valde. Efter att testdatan bundits till rätt `Comfort`-beslut återstod ett kötest: dess nyliga Comfort-cykel flyttade korrekt nästa hygienfrist och gjorde profilen till `Eco`. Cykeln placerades därför inom ett giltigt men snart förfallet hygienfönster. Den isolerade omkörningen gav 9/9 och den slutliga fokuserade sviten 131/131. Detta var fel i felinjektionen, inte produktfel.

Frontendkällan ändrades inte. Föregående verifierade resultat, 216 Vitest-tester och 26 tillämpliga Playwright-flöden med 6 projektdubbletter överhoppade, kördes därför inte om och räknas inte som nya kontroller här.

## Kvarvarande gränser

- En beständig revisionskedja till 2R2C-/COP-modellernas exakta träningsurval och logiska träningsversion är nu lokalt implementerad i [den efterföljande modellprovenansrapporten](2026-09-01-thermal-model-provenance-verification.md). Automatisk återhashning av historiska modellrader och bindning till signerad build-digest återstår.
- InMemory-regressionerna bevisar kontraktet men inte PostgreSQLs verkliga serialiseringskonflikter. En driftlik integration med två samtidiga DbContext/transactions behövs.
- Den sista databaskontrollen kan inte vara atomisk med en fysisk HA/P1P2- eller ONECTA-skrivning. Befintlig lease, bekräftelse och safe-zero minskar men eliminerar inte det externa intervallet.
- Verklig HA/EMHASS/prognos/pris och representativ DHW-historik måste fortsatt observeras i Shadow enligt readinesskraven. Den här leveransen är kodverifiering, inte aktivt driftgodkännande.
