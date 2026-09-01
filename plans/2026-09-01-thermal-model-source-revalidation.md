# Automatisk omvalidering av termiskt modellunderlag 2026-09-01

## Resultat

Varje läsning som kan godkänna en aktiv 2R2C- eller COP-modell väljer nu om modellens exakta kontoägda historikrader med samma versionsbundna regler som vid träningen och räknar om källbeviset. Ändrad eller raderad råhistorik, en retroaktivt tillagd kvalificerande rad, ändrad aktiverad rums-/entity-konfiguration eller ändrad verifiering av effektmätningen gör modellen omedelbart ej verifierad. Ett annat kontos rader kan varken ändra urvalet eller skapa en falsk tidsdubblett.

Kontrollen är inkopplad i planeringens första läsning, dess kontroller före och efter solver, lagrad plans konsumtionsgräns, readiness samt det autentiserade modell-API:t. Den gör inga databasändringar och kan inte aktivera styrning. Produktion berördes inte: ingen migration, app, worker, container, HA-/P1P2- eller ONECTA-skrivning startades. Legacy-DHW-koden är oförändrad och tidigare verifierad produktion förblir `ControlMode=Legacy` / `DhwWriter=Legacy`, med LWT och FullActive avstängda.

## Implementation

- Träningsjobben och omvalideraren använder gemensamma deterministiska urvalsmetoder för 2R2C och COP. Konto, urvalsfönster, dubblettider, kvalitetsmetadata, faser och fysiska konsistenskontroller behandlas därför likadant vid träning och senare kontroll.
- Omvalideraren läser bara det verifierade kontots råtelemetri och återskapar varje versions sparade urvalsfönster, tränings-/valideringsdelning och relevanta konfigurationsfingeravtryck.
- En exakt matchning rapporteras som `Current`. Saknat/oläsbart versionsbevis rapporteras som `Unproven`; ändrat eller ej återskapningsbart underlag som `Changed`. Den gemensamma modellbedömningen översätter en ändrad källa till `SourceChanged` och stoppar fortsatt användning.
- Planeringsfingeravtrycket innehåller fortfarande modell och konfiguration. Den nya omhashningen körs vid varje befintlig läs-/omläsningsgräns, så en historikändring under solverarbetet kasserar resultatet innan planpersistens.
- Modell-API:t lämnar en separat, tidsstämplad `sourceValidation` men aldrig råa sample- eller konfigurationshashar, konto-ID eller `SourceEvidenceJson`.
- API:t omvaliderar upp till de 100 modellversioner som redan returneras med en gemensam konto-/tidsavgränsad historikläsning. Planeringen omvaliderar endast den valda aktiva 2R2C- och COP-versionen.

## UX/UI

- Modellvyn kräver både strukturellt källbevis och en högst fem minuter gammal lyckad omvalidering innan modellen kan visas grön.
- `Källbevis saknas`, `Källunderlaget är inte omverifierat` och `Källunderlaget har ändrats · träna om modellen` är separata tillstånd med olika åtgärder.
- Ett ändrat underlag döljer modellmått och avancerade parametrar som aktuella bevis, men versionen ligger kvar synlig för revisionsspårbarhet.
- Desktop- och 320-pixels mobilflödet verifierar ändrat underlag, lyckad omvalidering, läsfel, tangentbordsåterhämtning och att inga mutationsanrop görs.

## Kontroller

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release-build | Godkänd utan varningar eller fel |
| Direkta källomvalideringstester | 9 godkända, 0 misslyckade |
| Fokuserad plan/readiness/API/källsvit | 122 godkända, 0 misslyckade |
| Full backendsvit | 1 174 godkända, 6 befintliga undantag, 0 misslyckade |
| Fokuserad Vitest-svit | 39 godkända, 0 misslyckade |
| Full Vitest-svit | 226 godkända, 0 misslyckade |
| TypeScript/Vite-produktionsbygge | Godkänt |
| Fokuserad Playwright desktop/mobil | 2 godkända, 0 misslyckade |
| Full Playwright-svit | 26 tillämpliga godkända, 6 projektdubbletter överhoppade |
| Mappbaserad whitespace-formattering av ändrade C#-filer | Godkänd |
| `git diff --check` | Godkänd före dokumentationscommit |

Test-fixtures för planering använde tidigare syntetiska SHA-256-värden utan motsvarande historikrader. Den nya kontrollen stoppade dem korrekt. Fixtures har nu 489 verkligt valda femminutersrader, persistenta konfigurations-ID:n och källbevis skapade med produktionskoden. Tester som avser aktuell livepunkt hämtar uttryckligen senaste rad och blandar inte längre ihop den med träningshistoriken.

## Kvarvarande gränser

- Verklig frågetid, indexanvändning och samtidiga skrivkonflikter är inte mätta mot driftlik PostgreSQL. Omvalideringen är korrekt fail-closed i EF-/HTTP-/planeringsregressionerna men behöver en isolerad PostgreSQL-acceptans innan nästa release.
- När retentionen har rensat någon rad som en gammal modell hänvisar till blir versionen avsiktligt `Changed` och måste ersättas. Nattlig träning förväntas hålla den valda modellen betydligt yngre än rådataretentionen.
- Logiska algoritmversioner är fortfarande inte bundna till signerad container- eller commitdigest.
- Den sista planomläsningen minskar men kan inte göra PostgreSQL-läsning och en fysisk HA/P1P2-skrivning atomiska. Writer-lease, safe-zero och återkopplingsverifiering kvarstår som skydd; verkliga nätverks-/konfliktgränser ska provas separat.
- Verklig kontoägd HA-historik, Shadow-period, representativt väder, grundkurva, komfort, DHW-/hygiencykler och normaliserad kostnadsjämförelse återstår. Denna leverans är kodverifiering, inte aktiveringsgodkännande.
