# PostgreSQL-provet låst till lokal Docker – 2026-09-04

## Avgränsat resultat

Säkerhetskontrollen för PostgreSQL-anslutningssträngen skyddade redan databasen, men den ursprungliga harnessen använde implicit vald Docker-värd vid containerstart och städning. Ett fjärrcontext eller miljöoverride kunde därför skapa testcontainern på fel värd innan databaskontrollen ens kördes. Det har inte observerats i drift; luckan hittades genom lokal kodgranskning.

`scripts/test-postgres-acceptance.ps1` använder nu en explicit tillåten lokal socket för samtliga Docker-operationer. Standard är Docker Desktops lokala Linux-pipe på Windows. Bara två kända lokala pipes och två vanliga lokala Unix-socketvägar accepteras; TCP, SSH, fjärrpipes, egna socketvägar, extra whitespace och andra värden stoppas före enginekontakt. Inget försök görs att ändra eller reparera Docker.

- `DOCKER_CONTEXT` och `DOCKER_HOST` måste vara frånvarande/tomma i testprocessen. Avvisning skriver inte ut deras värden eller den felaktiga endpointen.
- Docker körs via sitt en gång upplösta programnamn och en gemensam `--host`-prefixarray, också i readiness-underprocessen och `finally`-städningen. Byte av användarens valda context kan inte byta mål för dessa kommandon.
- `-SafetyCheckOnly` kontrollerar gränsen och lämnar ett sanerat resultat utan att leta upp Docker, kontakta engine, skapa container, ansluta databas eller ändra testdatabasens miljövariabel.
- Den nya fristående PowerShell-sviten provar fyra tillåtna sockets, tretton otillåtna/tvetydiga endpoints och båda miljöoverrides. AST-kontroller täcker alla fyra normala Docker-anrop, readiness-underprocessen och att prefixet inte binds om.
- PR-workflowen har en separat `pwsh`-kontroll för harnessen utan Docker. Riktig PostgreSQL körs fortfarande endast genom opt-in-harnessen; gröna skyddstester är inte ett substitut.

## Verifiering

| Kontroll | Resultat |
| --- | --- |
| `pwsh -NoProfile -File scripts/test-postgres-harness-safety.ps1` | 51 godkända kontroller, exit 0, ingen engine-/databaskontakt |
| Samma säkerhetssvit med två ärvda syntetiska Docker-miljövärden | 51 kontroller godkända; båda ursprungsvärdena återställdes efteråt |
| `pwsh -NoProfile -File scripts/test-postgres-acceptance.ps1 -SafetyCheckOnly` | Tillåten lokal Desktop-pipe och `EngineContacted=False`, exit 0 |
| PowerShell AST för harnessen | Inga syntaxfel; varje Docker-anrop använder låst endpoint |
| `git diff --check` | Godkänd |
| Verklig PostgreSQL 17, p95, indexplan och serializable-konflikt | Inte körda i denna uppföljning |
| CI, image, produktion och Shadow | Inte körda/ändrade i denna uppföljning |

Applikationens C#-/frontendkällkod ändrades inte. De tidigare 1 198 backend-, 233 UI- och 26 browserresultaten i [byggproveniensrapporten](2026-09-04-thermal-build-provenance-verification.md) är tidigare verifiering och redovisas inte som omkörda här. Inga nya beroenden installerades. Ingen push, PR, deploy, migration, konto-/credentialändring eller HA-/P1P2-/ONECTA-skrivning gjordes; Legacy-DHW och driftlägen lämnades orörda.

## Nästa acceptanssteg

Den senast verifierade lokala Docker-enginen var otillgänglig. Den har inte startats, reparerats eller omproberats i denna tooling-uppföljning. Kör den riktiga harnessen först med en fungerande lokal engine och repoets konfigurerade .NET SDK; använd inte produktionsdatabasen som ersättning. Verifiera därefter riktig indexanvändning, svarstid och konfliktbeteende, och fortsätt övrig HA-/Shadow-/DHW-acceptans enligt huvudplanen. Linuxgrenen är struktur-/preflighttestad lokalt men en faktisk Linux-/CI-processkörning återstår.

`README.md`, huvudplanen och PostgreSQL-acceptansrapporten är uppdaterade. De gemensamma infrastrukturdokumenten och Windows Kontrollerad mappåtkomst har inte ändrats.
