# Modellunderlag och verifierade uppvärmningsdygn – 2026-08-31

## Resultat och avgränsning

Fortsättningen utgår från lokal commit `29c7922` på `codex/session-recovery-regressions`. Den skärper modellträningens validering, flera beredskapskrav och modellvyns förklaringar. Slutkontrollerna slutfördes efter återupptaget arbete på eftermiddagen. Ändringen är **lokal källkod**, inte publicerad, driftsatt eller ett godkännande av intelligent styrning.

Legacyalgoritm, återkommande jobbtider, ONECTA-payloads, aktiv regulator, writer-handover, databasmodell och migrationer är oförändrade. Inga produktionskonton, credentials, inställningar eller aktiveringsspärrar ändrades. Produktionens separata read-only-kontroll beskrivs nedan.

## Modellträning och validering

- Gemensam, läsande bedömning används av modellträning, beredskap och modell-API. Den kräver tolkningsbara objekt, entydiga fält, rätt typer, ändliga och icke-negativa felmått, rimliga parametrar, giltig träningsperiod och möjliga punktantal. Äldre format utan `validationVersion=1` blir **ej verifierade**; en `IsActive`-markering räcker inte. En GET ändrar inte modellens markering eller driftläge.
- Husmodellens sista 20 procent är undanhållna från både parameterträning och val av vind-/solpåverkan. Vid tillräcklig vädertäckning väljs dessa funktioner på en separat intern valideringsdel. Grundkurva och rumskalibrering använder enbart träningsdelen, inte den slutliga valideringsperioden.
- Tvåtimmars- och dygns-MAE beräknas över **hela 24 respektive 288 femminuterssteg**. Luckor och dubbla tider ger inte sammanhängande valideringsfönster. Ett för kort underlag förkortas aldrig till ett påstått dygn. Antalet faktiskt utvärderade fönster sparas additivt i modellens befintliga JSON-metadata. Utan kompletta fönster kan modellen inte godkännas.
- Redan sparade telemetrirader kontrolleras före träning: konfigurerade givare, kvalitet/exkludering, ändliga värden, intervall, kända driftfaser, tidsstämplar och överensstämmelse mellan flöde, temperaturdifferens, värmeeffekt och COP. Dubbla och framtida tidsstämplar utesluts. Giltig historikimport får användas för träning men aldrig intyga verklig liveinsamling eller uppvärmningsdygn.
- Vid verifierad DHW-drift behålls rummens observerade temperaturutveckling med **noll tillförd husvärme**; tankens värmeeffekt och LWT används inte som husvärme/grundkurva. Detta följer planens antagande om en kompressor och gemensam hydraulisk zon. Annars skulle ett dagligt varmvattenjobb ta bort alla kompletta dygnsfönster. Antagandet måste verifieras för installationen innan aktiv styrning.
- COP-träning kräver bekräftat effekttecken även vid direkt anrop till träningsjobbet samt uttryckligen avstängd elpatron och avfrostning. En underkänd ny kandidat ersätter inte föregående modell. Saknade driftfaser gissas inte vara avstängda.

## Beredskap och mätta uppvärmningsdygn

- En sammanhängande Shadow-period rekonstrueras från den befintliga lägeshistoriken. Återgång till Legacy eller motsägande/okänd övergång nollställer underlaget. Gamla Shadow-poster före en återgång räknas inte som fortsatt verifieringsperiod.
- Ett räknat uppvärmningsdygn är ett **avslutat lokalt kalenderdygn** inom aktuell period, med minst 98 procent godkända, unika femminuterspunkter och minst två intilliggande verifierade husvärmepunkter. Pågående/ofullständiga dygn, importer, framtida rader, enstaka isolerade punkter och enbart DHW/avfrostning räknas inte. Detta är en explicit evidensdefinition, **inte** ett krav på 24 timmars sammanhängande värmedrift eller ett nytt sex-timmarskrav.
- Svensk sommar-/vintertid testas med **276, 288 och 300 femminuterspunkter** per kalenderdygn. Okänd tidszon ger inget godkännande.
- Grundkurveunderlaget kräver dessutom bibehållen nedre komfortgräns i kritiska rum och giltig uppmätt `HeatingDeviation` nära noll i dygnets godkända punkter. Collectorn sparar nu detta läsvärde som `heatingDeviationC` i befintlig kvalitets-JSON. Detta är inte ett styrkommando. Manuell grundkurvebekräftelse behövs fortfarande.
- 21-dagarstäckning räknar unika giltiga livepunkter inom perioden. Shadow-planer räknas högst en gång per kvart; dubbletter, framtida planer och ogiltiga solver-/giltighetsvärden kan inte fylla täckningskravet.
- Väderprognosen behöver aktuell kvalitet och sammanhängande täckning från nu med högst en timmes mellanrum. En ensam punkt långt framåt, feltypade temperaturer eller luckor får inte bli en godkänd 24-timmarsprognos.

## API och UX/UI

- `GET /api/thermal/models` behåller sina befintliga fält och får additivt `validation` med status, förklaring, bedömningstid, felmått och fönsterantal. Samma signerade konto och åtkomstfilter gäller. Isolerade HTTP-tester verifierar 401, kontoseparation och oförändrad lagring/Legacy.
- Modellvyn skiljer databasens aktivmarkering från verifierbar modellkvalitet och från tillåtelse till aktiv styrning. Äldre eller underkända kandidater behålls synligt med förklaring, utan grön falsk trygghet. Modellens dataperiod presenteras inte som antal verifierade uppvärmningsdygn.
- Läsfel döljer cachade godkännanden. **Hämta underlag igen** gör endast GET-anrop. En lokal klocka drar tillbaka äldre godkännanden även utan lyckad pollning. Råa serversvar visas inte som feltext.
- Observerad COP beräknas som summa värmeeffekt delat med summa eleffekt för godkända femminuterspunkter, inte medelvärdet av enskilda COP-tal. Mätverifiering, kvalitet, driftfaser, källtyp, konsistens, dubbla tider och ålder kontrolleras. Antal giltiga punkter visas; luckor fylls inte i och resultatet påstås inte vara en komplett dygnsmätning.
- Avancerade modell- och rumsparametrar ligger bakom utfällbara sektioner. Svenska enheter, textstatus, tangentbord och mobilstapling är testade. Tillgänglighetstestet fångade både numeriska mätvärden felaktigt märkta som rubriker och MUI-sektionernas rubriknivå; båda rättades utan att ändra utseendet på mätkorten.
- Slutliga bilder av verifierat desktopunderlag och saknat modellunderlag på 320 px granskades visuellt. Browserfixturens **Shadow** är syntetiskt testdata; produktionen är fortsatt **Legacy**.

## Kodverifiering

| Kontroll | Slutresultat |
|---|---|
| .NET 10 Release-bygg med `ContinuousIntegrationBuild=true` | Godkänt, inga rapporterade varningar/fel |
| Sista fokuserade `HomeAssistantCollectorValidationTests` | **17/17**, 0 fel |
| Hela backendens Release-svit | **980 godkända, 6 befintliga undantag, 0 fel**, totalt 986 |
| `npm.cmd test` | **200/200**, 16 testfiler |
| `npm.cmd run build` och bygget i sista E2E-körningen | TypeScript/Vite godkända |
| `npm.cmd run test:e2e` | **24 godkända, 6 befintliga projektexkluderingar, 0 fel**, totalt 30 |

Det är 109 fler backendfall och 29 fler UI-fall än sensorvalideringens 871/6 respektive 171. De två nya browserfallen kör modellvyn på desktop och mobil och verifierar att inga mutationer skickas.

Innan rättningen reproducerade 29 nya modell-/beredskapsfall verkliga fel; tre befintliga fall passerade. Utökade tester tillkom efter den fokuserade gröna körningen. En mellanliggande full backendkörning hade 978 godkända och två fel: den nya collectorfixturen försökte publicera en andra startbild i en redan ansluten session. Den byttes till en riktig `ApplyEvent` med kontrollerat returvärde; cachens avsiktliga skydd försvagades inte. Hela sviten kördes sedan om till slutresultatet ovan. Tidigare UI-mellankörningar fångade testklockans upplägg och de faktiska rubrikbristerna; slutlig helsvit och browserbygge är omkörda efter rättningarna.

Backendkommandon från repositoryroten:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.sln /t:Build /p:Configuration=Release /p:ContinuousIntegrationBuild=true /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true '/p:VSTestTestCaseFilter=FullyQualifiedName~ThermalModel|FullyQualifiedName~CopModelTests|FullyQualifiedName~ThermalReadinessEvidenceTests' /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release '/p:VSTestTestCaseFilter=FullyQualifiedName~HomeAssistantCollectorValidationTests' /verbosity:minimal
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontend: `npm.cmd test -- modelEvidence ThermalModelPage`, `npm.cmd test`, `npm.cmd run build` och `npm.cmd run test:e2e` från `frontend`. `git diff --check` kördes också. Lokalt används installerad SDK **10.0.111**, medan repo/CI/Docker förblir låsta till **10.0.400**. Ingen beroendeändring, ny restore, CI-körning, push, publicering, containerbyggnad eller full applikationsstart ingick. Vites genererade artefakter och browserbilder är ignorerade lokala filer.

De sex backendundantagen är oförändrade: två BatchRunner-persistenstester, tre ScheduleHistory-integrationstester och ett live-Nordpool-test. JSDOMs befintliga färgkontrastundantag är kvar. Testerna använder syntetiska mätningar, EF InMemory och isolerade HTTP-/browserfixturer, inte produktionsdatabas eller fysiska styrklienter. Full hjälpmedels-, PostgreSQL- och nätverksacceptans återstår.

## Produktion – separat read-only-evidens

En tidigare avläsning under arbetet **11:13–11:17 CEST / 09:13–09:17 UTC** visade oförändrade images, noll omstarter, frisk hälsa, anonymt skydd, Legacy/Legacy och noll styrkommandon. Inga nya skrivmarkörer fanns mellan 08:07:44 och 09:15:15 UTC.

Förnyad kontroll **17:35–17:38 CEST / 15:35–15:38 UTC**:

- `daikin-prisstyrning-1`, PostgreSQL och EMHASS körde med noll omstarter och samma image-referenser som [produktionsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session`: HTTP 200. Sessionen var oautentiserad med booleskt verifierad CSRF-utgivning. Skyddad thermal-status, HA-status och HA-katalog gav 401.
- Explicit `BEGIN TRANSACTION READ ONLY` bekräftade en konfiguration i **Legacy/Legacy** och **noll termiska styrkommandon**.
- Ordinarie 13:35 CEST-jobb startade 11:35:02.493 UTC och slutfördes 11:35:02.893 UTC med två behandlade record och noll fångade jobbundantag. Recordet som tidigare skrev framgångsrikt hade `generated=False`; loggen bekräftade att ingen schemaläggbar åtgärd fanns. Detta är **inte en ny lyckad skrivning**.
- `Apply failed` 11:35:02.791 UTC matchade samma äldre misslyckade record som i 01:35-körningen. Matchningen gjordes i minnet och endast booleska resultat redovisades, inga konto-ID:n. Felet är kvar och kontoinställningarna ändrades inte. Att jobbsammanfattningen har noll undantag betyder inte att alla API-skrivningar lyckades.
- Inga `apply OK`/`Applied`-markörer fanns mellan 09:15:15 och 15:36:19 UTC. Tidigare verkligt accepterad API-skrivning 01:35 CEST förblir tidigare verifiering, inte bevis för fysisk varmvattenuppvärmning.
- Inga extrajobb, testsändningar, deployer, omstarter eller lägesbyten utlöstes. Inga credentials/cookies/råloggar visades. Första read-only-inspektionen hade ett citeringsfel i Docker-formatsträngen; korrigerad citering gav resultaten ovan utan ändring av containrarna.

## Nästa avgränsning och kvarvarande acceptans

1. Modellbedömningen vid optimerare/koordinator är nu införd och lokalt testad i [efterföljande konsumentrapport](2026-08-31-planning-model-consumption-verification.md), fortfarande utan driftsättning. Slutlig solver-/planvalidering, konsumtion av redan sparade planer och revisioner vid fysisk skrivning återstår. Ett modell-API och en grön syntetisk svit är inte verifiering av hela den aktiva styrkedjan.
2. Bind sparade modeller och telemetri till verifierad konfigurations-/entity-revision. Dagens kvalitetsmetadata kan inte återskapa all råproveniens för äldre rader. En hel fler-entity-import, samtidiga imports/liveinsamling och omstartskontinuitet behöver egen granskning. Processlokala lås och sensorhälsa är inte en flerinstanslösning.
3. Utvärdera representativ träningsdata och driftfaser i verklig anläggning. Avfrostningsluckor får fortfarande inte beskrivas som kompletta dygn; installationer med sådana faser kan behöva vidare modellarbete. Nuvarande uppvärmningsdygnskrav bevisar observation och husvärme, inte att väderförhållandena varit representativa. Återstående DHW-/hygiengrindar är inte slutgranskade här.
4. Kontoägd HA-konfiguration, inloggad produktions-UX, PostgreSQL-/nätverksprov, verkliga Shadow-dygn, grundkurveprov, DHW-/hygiencykler och normaliserad besparingsjämförelse återstår. LWT/FullActive får inte aktiveras utifrån denna leverans.
5. Granska den samlade lokala serien och kör tillämplig CI före en uttryckligt godkänd release genom **samma Dockhand-stack** med befintlig rollbackväg. Den äldre adminraderingen är fortfarande inte säker i den driftsatta versionen; använd inte den. Ingen publicering eller driftsättning gjordes här.

Huvudplanen, produktionsrapporten och sensorvalideringsrapportens nästa steg uppdaterades. De gemensamma `README.md`/`INFRASTRUCTURE.md` under Dokument är oförändrade på grund av Kontrollerad mappåtkomst; befintlig förberedd patch finns kvar. Skyddet har inte ändrats eller kringgåtts. Meningsfullt godkänt implementationsarbete återstår; fortsättningsautomationen lämnades oförändrad.
