# Verifiering av termisk modellprovenans 2026-09-01

## Resultat

Varje ny 2R2C- och COP-modellversion får nu ett beständigt källbevis för det exakta kontoägda historiska urval som användes. Beviset binder valda telemetrirader, relevant aktiverad rums-/entity-konfiguration, urvalsregel, logisk algoritmversion, tidsfönster samt tränings- och valideringsantal med SHA-256.

En modell utan komplett och internt konsekvent bevis kan inte längre bli godkänd av den gemensamma modellbedömningen. Det gäller även äldre modeller som fortfarande har `IsActive=true`: databasens aktivmarkering är inte ett säkerhetsgodkännande. Den additiva migrationens standardvärde är därför det giltiga JSON-objektet `{}`, vilket medvetet gör befintliga versioner **ej verifierade** tills nattlig träning har skapat en ny version.

Ingen migration kördes mot någon databas och ingen app eller worker startades. Ingen driftsättning, kontoändring, HA-/P1P2-skrivning eller ONECTA-skrivning gjordes. Legacy-DHW-koden ändrades inte; tidigare verifierad produktion är fortsatt `ControlMode=Legacy` / `DhwWriter=Legacy`, med LWT och FullActive avstängda.

## Implementation

- Det persistenta `SourceEvidenceJson` lagras som `jsonb` på `ThermalModelVersions` genom den additiva migrationen `AddThermalModelProvenance`.
- Telemetrifingeravtrycket omfattar modelltypens faktiska källfält, rad-ID, tid, driftfaser och sparad kvalitetsmetadata. För 2R2C ingår även rumstemperatur, uteklimat, vind/sol och hydraulik; för COP ingår köldbärare, LWT/RWT, flöde, värme-/eleffekt och COP.
- Konfigurationsfingeravtrycket omfattar kontot, aktiverade rum och entities samt COP-modellens verifierade effekttecken. Fel konto, noll-/dubblett-ID, dubblettid, dubblettroll/entity eller inaktiverad konfiguration avvisas innan en version skapas.
- `grey-box-2r2c-v1` och `ridge-cop-v1` skiljs från de versionsbundna urvalsreglerna `thermal-validated-history-v1` och `cop-validated-history-v1`. Namnet lovar inte att importerad, uttryckligt kvalitetsgodkänd historik är liveinsamlad.
- Modellbedömningen kräver rätt schema-, algoritm- och urvalsversion, giltiga tidsrelationer, positiva SHA-256-värden och samma tränings-/valideringsantal som modellmåtten.
- API:t lämnar bara en ofarlig sammanfattning: verifierbarhet, versioner, urvalsfönster och antal. Konto-ID och råa telemetri-/konfigurationsfingeravtryck exponeras inte.
- Browsern gör en egen fail-closed kontraktskontroll. Ett äldre eller motsägelsefullt API-svar kan inte visas som validerat bara för att dess `validation.passed` är sant.

## UX/UI

- Modellens vanliga statuskort säger på svenska om träningsunderlaget är spårbart och visar antal valda mätpunkter.
- Versionslistan skiljer `Spårbart källurval` från `Källbevis saknas · modellen måste tränas om`.
- Logisk algoritmversion, urvalsregel, källperiod och delmängdsantal ligger bakom **Avancerat**, medan den primära vyn använder vardagsspråk.
- Saknat källbevis tar bort grönt modellgodkännande även om en äldre aktivmarkering eller cache säger annat.
- Desktop- och 320-pixels mobilbilder granskades efter testkörningen. Status, källantal och Avancerat-information är läsbara utan sidscroll. Browserns Shadow-status är syntetisk testdata, inte produktionens driftläge.

## Kontroller

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release-build | Godkänd utan varningar eller fel |
| Fokuserad modell/proveniens/readiness/plan/migrationssvit | 212 godkända, 0 misslyckade |
| Full backendsvit | 1 157 godkända, 6 befintliga undantag, 0 misslyckade |
| Fokuserad Vitest-svit | 34 godkända, 0 misslyckade |
| Full Vitest-svit | 221 godkända, 0 misslyckade |
| TypeScript/Vite-produktionsbygge | Godkänt |
| Playwright desktop/mobil | 26 tillämpliga godkända, 6 projektdubbletter överhoppade |
| Mappbaserad whitespace-formattering av 12 ändrade C#-filer | Godkänd |
| `git diff --check` | Godkänd före dokumentationscommit |

Den första fulla browserkörningen gav 24 godkända och två modellfel. Modellfixturen saknade det nya källbeviset samtidigt som den påstod `Validated`; UI:t avvisade detta korrekt. Fixturen fick realistisk proveniens, det fokuserade desktop-/mobilflödet passerade 2/2 och därefter passerade hela Playwright-sviten 26/6.

EF-verktyget 10.0.2 varnade för att det är äldre än runtime 10.0.11 men genererade migrationen. En första `--no-build`-generering utgick från den föregående byggda modellen och föreslog tom sträng som `jsonb`-standard. Den ännu okörda migrationen togs bort, modellen byggdes om och migrationen genererades på nytt med `{}`. Slutlig migrationsdriftkontroll passerar; ingen databas berördes.

## Kvarvarande gränser

- Det lagrade beviset är ett reproducerbart kryptografiskt åtagande till träningsögonblickets urval. Historiska rader återhashas inte automatiskt vid varje planläsning. Om administrativ redigering av råhistorik ska stödjas behövs en separat kontoägd revisions-/omvalideringstjänst.
- Algoritmversionerna är explicita kodkontrakt, inte signerade container- eller commit-digests. Varje semantisk träningsändring måste höja rätt algoritm- eller urvalsversion; en framtida build-digestbindning kan ge starkare leveranskedjebevis.
- InMemory- och migrationsdrifttester bevisar inte verkliga PostgreSQL-konflikter, lås eller driftmigrationstid. Dessa ska provas i en isolerad driftlik PostgreSQL före nästa release.
- Verklig kontoägd HA-konfiguration, representativ historik, Shadow-dygn, grundkurva, komfort, DHW-/hygiencykler och normaliserad kostnadsjämförelse återstår. Denna leverans är kodverifiering, inte godkännande av LWT eller FullActive.
