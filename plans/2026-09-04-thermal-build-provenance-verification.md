# Termiska modeller bundna till körande kodrevision – 2026-09-04

## Resultat och avgränsning

2R2C- och COP-modeller binds nu till källkodsrevisionen som är inbakad i den körande .NET-binären. En saknad revision eller en modell från en annan revision kan inte godkänna planering, readiness eller aktiv plankonsumtion. UI förklarar omträning som en egen åtgärd när kodrevisionen har ändrats.

Detta är en lokal kodleverans på `codex/session-recovery-regressions`, ovanpå `e82d21c`. Ingen push, PR, publicering, driftsättning, migrationskörning, produktionsavläsning, app-/workerstart, kontoändring eller HA-/P1P2-/ONECTA-skrivning ingick. Tidigare produktionsbevis i [driftjournalen](2026-08-30-production-verification.md) är historiska, inte en ny hälsokontroll. `Legacy/Legacy` är fortsatt det enda driftgodkända läget; befintlig schemalogik, payload och inställningsbetydelse ändras inte.

## Implementation

- `RuntimeBuildProvenance` läser exakt ett `Prisstyrning.SourceRevision`-attribut ur applikationens assembly. Endast fullständiga 40-/64-teckens hexrevisioner accepteras, aldrig tomma värden, taggar, dubbla attribut eller nollplatshållare. Runtimekonfiguration och miljövariabler kan inte ersätta identiteten.
- MSBuild genererar attributet även med projektets befintliga `GenerateAssemblyInfo=false`. Docker och CI kräver revision; vanliga lokala .NET-byggen utan stämpel fungerar fortfarande, men kan inte skapa godkänt termiskt modellunderlag. Lokal stämpelkontroll använde en uttryckligt syntetisk testrevision, inte en releasemärkt smutsig checkout.
- Nya `SourceEvidenceJson` använder schema 2 med `BuildRevision`. Ingen ny EF-migration behövs för denna ändring. Äldre schema-1-rader finns kvar men kräver omträning; inget historiskt bevis skrivs om för att se godkänt ut.
- Samma revisionskontroll används i träningsjobben, modell-API:t, readiness, koordinatorn, EMHASS-workerns kontroll före/efter beräkning, lagrad plans läsning och den sista skrivgränsen. `BuildChanged` skiljs från ändrat historiskt underlag och från saknat bevis.
- Varje byte av källkodsrevision kräver konservativt omträning, även efter ändringar utanför värmealgoritmen. Detta är avsiktligt tills en smalare kompatibilitetspolicy kan styrkas. Modellernas aktiva databasmarkeringar är inte ett aktiveringsgodkännande.
- PR-bygget använder `github.sha`, alltså den faktiskt utcheckade merge-revisionen, inte en avvikande PR-head. Containerbygget bäddar in samma värde och anger OCI-revisionsetikett.
- Containerworkflowen är förberedd för signerad build-attestering av den publicerade imagen med `actions/attest@v4`. Runtimekontrollen intygar endast en källkodsidentitet, **inte** att en signatur eller image har verifierats. Signerad workflowidentitet, godkänd commit och exakt digest ska verifieras separat före en framtida release. Ingen sådan ny attestation har skapats eller verifierats här.

Workflowkontraktet kontrollerades mot [actions/attest](https://github.com/actions/attest) och [GitHubs officiella verifieringsanvisning](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations). Repositoryts offentliga synlighet bekräftades med en skrivskyddad `gh repo view`; inga GitHub-mutationer gjordes. Storage-record-steget är avstängt för det personägda repositoryt. Ingen lokal `actionlint` fanns; faktisk GitHub Actions-/registrykörning återstår.

## UX/UI

- Modellvyn skiljer ändrad kodrevision från ändrad historik och visar konkret krav på omträning. Tidigare godkända MAE-värden och avancerade parametrar döljs som aktuella bevis när modellen underkänns.
- Avancerat visar en kort byggrevision, inte råa konto-/konfigurations-/telemetrifingeravtryck. API:t lämnar den fulla, ofarliga källkodsrevisionen.
- Klienten avvisar saknad, numerisk, nollfylld, icke-kanonisk eller ogiltig revision även om andra svarsfält påstår att modellen är godkänd.
- Browserflödet provar ändrad historik, godkänd omvalidering, ändrad kodrevision, läsfel och tangentbordsåterhämtning på desktop och 320-pixels mobil. Det kräver noll mutationsanrop och kontrollerar sidbredd. Lokala skärmbilder av omträning och avancerad revisionsvisning har granskats; det är inte produktions- eller full skärmläsaracceptans.

## Slutlig lokal verifiering

| Kontroll | Resultat |
| --- | --- |
| .NET 10 Release och full ombyggnad med revisionsstämpel | Godkända utan varningar/fel |
| Full backendsvit | 1 198 godkända, 7 överhoppade, 0 misslyckade |
| Byggspärr: saknad revision när den krävs | Avvisad med avsett MSBuild-fel |
| Byggspärr: `latest` och nollfylld revision | Båda avvisade med avsett MSBuild-fel |
| Genererad assemblymetadata | Exakt syntetisk testrevision återfanns i genererat attribut |
| `npm run build` | TypeScript/Vite godkända |
| `npm test` | 233 godkända i 19 filer, 0 misslyckade |
| `npx playwright test` | 26 tillämpliga flöden godkända, 6 befintliga projektexkluderingar |
| C# whitespace-formattering och `git diff --check` | Godkända |
| Lokal PostgreSQL 17-acceptans | Inte körd; lokal Docker-engine är otillgänglig |
| GitHub CI, publicerad image/attestation, verklig Shadow | Inte körda/godkända i denna leverans |

De sju backendundantagen är sex tidigare explicit överhoppade HTTP-/historiktester och ett tydligt opt-in-undantag för PostgreSQL. Inget undantag räknas som en passering. Den fulla backendsviten omfattar befintliga legacy-/payloadregressioner och nya tester för byggidentitet, gammalt schema, ändrad/saknad revision, readiness, API och avvisning vid solver-/skrivgränser.

Repo/CI/Docker anger SDK 10.0.400. Den installerade lokala SDK:n är 10.0.111, vars verktyg anropades uttryckligen; detta ersätter inte en kommande CI-körning med 10.0.400. Huvudkommandon:

```powershell
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll" Prisstyrning.sln /t:Rebuild /p:Configuration=Release /p:ContinuousIntegrationBuild=true /p:PrisstyrningSourceRevision=0123456789abcdef0123456789abcdef01234567 /p:PrisstyrningRequireSourceRevision=true /verbosity:minimal
dotnet exec "C:\Program Files\dotnet\sdk\10.0.111\vstest.console.dll" Prisstyrning.Tests\bin\Release\net10.0\Prisstyrning.Tests.dll --Logger:"console;verbosity=minimal"
```

Byggspärrarna provades med samma MSBuild-verktyg mot `Prisstyrning.csproj`, target `GeneratePrisstyrningSourceRevision`, först med `PrisstyrningRequireSourceRevision=true` utan revision, sedan med `latest` respektive 40 nollor. Samtliga gav avsett exit 1. En tidigare vanlig ostämplad Release-ombyggnad passerade också. Frontendkommandona ovan kördes från `frontend`; C#-formatteraren kördes mappbaserat endast på ändrade C#-filer.

Första helkörningen hittade en osparad readiness-testfixtur och frontendbygget ett test med `Array.at` utanför TypeScript-målets stöd. Båda rättades före slutkörningarna. Ett parallellt lokalt backend-/frontendbygge krockade när Vite ersatte `wwwroot`; ordningen ändrades till färdigt frontendbygge följt av backend-Rebuild, som passerade. Detta var lokal testorkestrering, inte ett produktionsfel. Ett tidigare PowerShell-försök att ladda net10-assemblyn via reflection saknade rätt runtime; genererad metadata och isolerade .NET-tester användes i stället.

Formatteraren gav även whitespace-/radbrytningsändringar i `Program.cs`. Den automatiska säkerhetsgranskningen stoppade återställningen av dem; formateringen lämnades därför kvar. Skrivskyddad jämförelse bekräftade att filens enda nya icke-whitespace-innehåll är singletonregistreringen för byggidentitet. Ingen tidigare autentisering, Daikinlogik eller aktiveringsspärr återställdes.

## Verifierad lokal miljögräns och nästa steg

2026-09-04 bekräftade `docker context show` kontexten `desktop-linux`, och `docker context inspect desktop-linux --format '{{.Endpoints.docker.Host}}'` visade den lokala namngivna pipen `npipe:////./pipe/dockerDesktopLinuxEngine`. `docker --context desktop-linux version --format '{{.Server.Version}}'` gav exit 1 eftersom pipen saknades. Ingen engine-start, Docker-reparation eller containerstart gjordes. Den äldre kraschdiagnosen finns kvar i [PostgreSQL-rapporten](2026-09-01-postgresql-thermal-acceptance.md); orsaken till just dagens avstängda engine utreddes inte på nytt.

1. Kör PostgreSQL-harnessen när en fungerande isolerad lokal engine finns. Verifiera lokal kontext före containerstart; använd aldrig produktionsdatabasen som ersättning. p95/indexplan och riktiga serializable-konflikter är fortfarande obevisade.
2. Granska den samlade lokala serien, kör tillämplig CI och verifiera signerad image, förväntad commit/workflow och exakt digest inför en separat godkänd release i befintlig Dockhand-stack. Bevara befintlig credentialnyckel, backup och Legacy-läge.
3. Fortsätt kontoägd HA-/historik- och nätverksacceptans, representativ modellträning, Shadow, grundkurva och fysiska DHW-/hygiencykler enligt huvudplanen. PostgreSQL och ett fysiskt styranrop kan fortfarande inte göras atomiska av dessa tester.

`README.md`, `AGENTS.md`, `MIGRATION.md`, huvudplanen och den lokala driftjournalen uppdateras med dessa gränser. De gemensamma infrastrukturdokumenten under Dokument och Windows Kontrollerad mappåtkomst lämnas orörda.
