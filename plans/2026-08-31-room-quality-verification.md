# Rumskvalitet och ordinarie legacykörning – 2026-08-31

## Resultat och avgränsning

Den ordinarie legacykörningen kl. **01:35:03 CEST** gav både `apply OK` och `Applied` för samma record som vid den tidigare godkända engångsverifieringen. Ett separat `Apply failed` gäller samma äldre record som tidigare. Jämförelsen gjordes utan att skriva ut konto-ID:n eller ändra inställningar. Detta verifierar en accepterad automatisk ONECTA-skrivning, inte en fysisk DHW-cykel.

Rumsvyns visning av givarkvalitet och händelsehistorik är rättad lokalt på `codex/session-recovery-regressions`, ovanpå `ac6e79d`. Ingen push, merge, deploy eller testsändning gjordes. Produktionens revision är fortsatt `233afa4`, med `ControlMode=Legacy` och `DhwWriter=Legacy`. Backend, databasmodell, legacyalgoritm, ONECTA-payloads och Compose är oförändrade i detta pass.

## Rättat i rumsvyn

- Informationshändelser om återhämtade givare visas som **Information**, inte som varningar. Information, Varning och Åtgärd krävs har både ikon och svensk text även på mobil.
- Händelser visas i en separat, daterad historik med tydlig förklaring att nivån avser när händelsen inträffade. Äldre varningar behålls; en ny informationshändelse antas inte automatiskt lösa alla tidigare problem. Historiska poster har inte `role=alert`.
- Aktuella rumskort använder `qualityJson`, inklusive `Excluded`, i stället för att tolka ett sparat numeriskt värde som bevis på en giltig givare. PascalCase/camelCase och både numeriska och namngivna kvalitetsvärden stöds.
- Ett kritiskt rums sparade reservvärde märks uttryckligen **Sparat reservvärde**. Det ger inte en aktuell komfortmarginal, även om temperaturen ser rimlig ut. En givare som återhämtar sig förblir exkluderad så länge backendens exkluderingsflagga är satt.
- Gammal data, nyligen importerad historik, saknad/felaktig metadata, ogiltiga mätvärden, framtida tidsstämplar och hämtningsfel kan inte bli gröna aktuella mätningar. Vid hämtningsfel döljs tidigare cachade rumsvärden och råa serverfel visas inte.
- En giltig men kall givare behåller sin negativa komfortmarginal och tydlig komfortvarning. Inaktiverade rum räknas inte som aktiva givarfel eller som giltiga aktiva rum.
- Sidan uppdaterar åldersbedömningen var 30:e sekund, även utan en lyckad ny datahämtning. Därmed inväntas inte nästa femminutershämtning för att upptäcka att visad data passerat tiominutersgränsen.
- Svenska decimaler, semantiska rumskort/rubriker och beskrivningslistor, datum i svensk tid med maskinläsbara UTC-tidsstämplar samt tangentbordsåtkomlig länk till hela händelseloggen används. Kort och etiketter klarar även 320 pixlars bredd utan horisontell sidscroll.
- Den lokala visuella testserverns standardsvar innehåller nu realistisk kvalitetsmetadata. Den anropar fortfarande inga verkliga integrationer.

## Kontroller och resultat

Tre nya inledande komponenttester reproducerade den saknade historikgränsen, felaktig reservvärdesvisning och aktuell komfortmarginal från gammal data. Efter rättning passerade de. Två senare testförväntningar behövde korrigeras: svensk talformatering använder Unicode-minus, och texten för okänd tid ingår i en sammansatt metadataetikett. Dessa var testantaganden, inte fel i regleringen.

- `npm test -- src/pages/thermal/ThermalRoomsPage.test.tsx`: först 3 reproducerade fel, därefter 3 godkända.
- Utökad fokuserad körning för rumsvy och presentation: 43 testfall; de två ovan nämnda testförväntningarna rättades före slutkörningen.
- Slutlig `npm test`: **60 godkända, 0 fel**, nio testfiler. De 43 tillkommande fallen består av 11 komponenttester och 32 datakvalitets-/presentationsfall. Axe ingår; det tidigare JSDOM-undantaget för färgkontrast är kvar. Detta är inte en full manuell skärmläsar- eller kontrastrevision.
- Fokuserad `npm run test:e2e -- --grep "rum skiljer"`: TypeScript/Vite godkända och båda desktop-/mobilfallen godkända.
- Slutlig `npm run test:e2e`: TypeScript/Vite godkända, **12 tillämpliga flöden godkända**, sex befintliga undantag för dubblerade desktop-/mobilprojekt, 0 fel. Rumstestet kontrollerar reservvärden, ett giltigt kallt rum, alla tre historiska nivåer, tidsstämplar, tangentbordsnavigering, smal layout och frånvaro av alla API-mutationer.
- Slutliga desktop-/mobilbilder granskades visuellt. De visar syntetiska data i en lokal testmiljö, inte produktionen eller godkänd Shadow-drift.
- Full backendregression på den tidigare byggda, oförändrade Release-assemblyn: **566 godkända, 6 befintligt överhoppade, 0 fel** (572 totalt). Backend byggdes inte om i detta frontendpass. Installerad lokal SDK 10.0.111 användes uttryckligen; repo/CI/Docker är fortsatt låsta till 10.0.400.
- `git diff --check`: godkänd. Separat diffkontroll bekräftade att C#-/projektfiler, jobb, migrationer och Compose-exempel inte ändrats.

Backendkommandot från repositoryroten var:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' exec 'C:\Program Files\dotnet\sdk\10.0.111\MSBuild.dll' Prisstyrning.Tests\Prisstyrning.Tests.csproj /t:VSTest /p:Configuration=Release /p:VSTestNoBuild=true /verbosity:minimal
```

Frontendkommandona kördes i `frontend`. Inga produktionsmigreringar, värmearbetare eller verkliga HA-/EMHASS-/Daikin-klienter startades av testerna.

## Read-only-produktionskontroll

Kontrollen genomfördes omkring **00:43–00:45 UTC / 02:43–02:45 CEST** den 31 augusti:

- App, PostgreSQL och EMHASS var `running` med noll omstarter. App-/EMHASS-digests matchade [driftsättningsrapporten](2026-08-30-production-verification.md).
- `/health/live`, `/health/ready` och anonym `/api/session` gav 200. Sessionen var `authenticated=false` med utfärdad CSRF-token; token- och cookievärden skrevs inte ut. Skyddad `/api/thermal/status` gav 401 utan session.
- En uttrycklig `BEGIN TRANSACTION READ ONLY`-fråga gav en thermal-konfiguration i `Legacy/Legacy` och noll thermal-styrkommandon.
- Ordinarie körning gav `apply OK` 2026-08-30 23:35:03.631 UTC och `Applied` 23:35:03.645 UTC, alltså 2026-08-31 01:35:03 CEST. Det separata misslyckade recordet loggades 01:35:01 CEST.
- En senare jämförelse av de två korta loggfönstren bekräftade samma lyckade respektive misslyckade record som vid engångsverifieringen 21:52 CEST. Inga konton eller credentials redovisades eller ändrades.

Ett tidigare kontrollförsök stoppades av godkännandestegets tillfälliga användningsgräns innan SSH exekverades. Efter att den angivna spärrtiden passerat godkändes den nya read-only-kontrollen. Ett citeringsfel i första godkända statuskommandot rättades; inga alternativa skyddsvägar eller driftmutationer användes.

## Nästa avgränsade uppföljning

**Statusradens samlade datakvalitet återstår att rätta.** Vid bildgranskningen syntes ett grönt sammanfattningsvärde samtidigt som ett rum korrekt visades som exkluderat. Källkodskontrollen bekräftade att `ThermalApiEndpoints.GetStatusAsync` beräknar `overallDataQuality` enbart från senaste telemetrins tidsstämpel, utan att läsa dess kvalitetsmetadata. Den nya rumsvyn rättar alltså inte denna separata API-/statusradsbrist.

Nästa arbete bör testa och rätta den samlade bedömningen utifrån aktiverade givare, kvalitetsflaggor, exkludering, importkälla och giltig tidsstämpel. Lägg till ett API-regressionsfall där en färsk snapshot med exkluderad kritisk givare inte får ge `Valid`. Bevara oförändrade regulatorer, driftlägen och legacy-DHW. Granska sedan statusrad och rumsvy tillsammans, inklusive felåterhämtning.

Dessutom återstår den tidigare dokumenterade adminraderingens livscykel, sex backendundantag, CI för de lokala revisionerna, verkligt autentiserat kontoflöde/HA-konfiguration och husets Shadow-/komfort-/modell-/DHW-verifiering. Ordinarie legacykörning är nu observerad; upprepa inte den engångsvisa testsändningen. Äldre kontoinställningar lämnas orörda utan verifierat kontoansvar och mandat.

Huvudplanen, den föregående lokala rapporten och driftverifieringen är uppdaterade i kodrepositoryt. De gemensamma dokumenten under Dokument är fortfarande blockerade av Kontrollerad mappåtkomst; den tidigare färdiga patchen är oförändrad. Skyddet har inte ändrats eller kringgåtts. Automationen har fortsatt meningsfullt godkänt arbete och lämnas aktiv.
