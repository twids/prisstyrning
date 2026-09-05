# EMHASS-status 2026-09-05

## Produktionsaktivering 17:50 CEST

Efter uttryckligt användargodkännande ändrades enbart `PRISSTYRNING_Emhass__Enabled` från false till true via befintliga Dockhand-stacken daikin. Compose-diff verifierade exakt denna ändring. Backup: `/mnt/user/appdata/dockhand/stacks/daikin/backups/compose.pre-emhass-enable-20260905T1545.yml`. Appen startade 15:50:51 UTC; EMHASS och PostgreSQL behöll starttiderna från 30 augusti. Runtime visar Enabled=true, ApplySchedule=true och AllowLwtActive/AllowFullActive=false. EMHASS är healthy och HTTP GET /get-config från själva appcontainern till http://emhass:5000 gav 200. Publik readiness gav 200 och anonym thermal-status 401. Read-only databastransaktion bekräftade Legacy/Legacy och noll termiska styrkommandon.

Detta verifierar aktiverad integration och anslutning, inte en genomförd optimering. Ingen Shadow-aktivering eller solver-jobb framtvingades. Nuvarande produktions-UI bygger fortfarande på tillgänglighetsflaggan som sätts först efter optimering och kan därför fortsatt visa nere. Den lokala UI-rättningen nedan har inte deployats.

Lokalt korrigerad missvisande status: `EmhassHealthState.Available` är falsk både innan första verifierade optimering och efter fel. Den kan därför inte ensam motivera etiketten ”nere”. API:t exponerar nu separat `emhassEnabled` från driftkonfigurationen. Statusraden och översikten visar avstängd integration uttryckligen; okänd tillgänglighet visas som ej verifierad. Äldre API-svar utan det nya fältet antas inte vara avstängda. Avstängd integration tar företräde över tidigare lyckad tillgänglighetsstatus.

Verifiering: `dotnet test Prisstyrning.Tests/Prisstyrning.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~ThermalStatusApiTests --verbosity minimal`: 13 godkända. `npm test -- --run src/components/thermal/ThermalStatusStrip.test.tsx`: 20 godkända. `npm run build`: TypeScript/Vite godkända. Testerna täcker avstängd, aktiverad, okänd samt äldre API-svar och bevarat Legacy-läge.

Ingen deploy, aktivering, produktionsändring eller testsändning utförd. Den tidigare produktionsrapportens lokala ändring bevarades. Full release-CI och driftsättning av denna rättning återstår; ingen full testsuite eller manuell browserkontroll kördes i detta pass. Produktionens senaste kontroller finns i produktionsrapporten och är tidigare evidens. Nästa ordinarie legacy-skrivning efter deploy återstår att verifiera.
