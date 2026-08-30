# Prisstyrning contributor guidance

## Project overview

Prisstyrning is an actively developed ASP.NET Core 10 application that builds price-aware domestic-hot-water schedules. It integrates Nordpool prices, Daikin ONECTA OAuth/device APIs, PostgreSQL via EF Core, and Hangfire jobs. The browser UI is a React 18/TypeScript/Vite application built into the backend's static `wwwroot/` directory.

## Repository map and boundaries

- `Program.cs` composes DI, middleware, minimal API endpoints, startup database migration, Hangfire, and SPA hosting. `Controllers/` contains MVC controllers.
- `ScheduleAlgorithm.cs`, `BatchRunner.cs`, the root `*Client.cs` files, and `Jobs/` contain scheduling and external-service logic.
- `data/Entities/` and `data/Repositories/` are the persistence model and access layer; `data/Migrations/` is EF Core migration history and the model snapshot.
- `frontend/` is the separately managed React application. Its differing commands and conventions are in `frontend/AGENTS.md`.
- `Prisstyrning.Tests/` is the xUnit test project. Unit, API, job, schema, and integration-style tests use in-memory databases and mocked HTTP where practical; a few HTTP infrastructure tests are explicitly skipped.
- `Daikin Api Doc/` contains checked-in upstream API reference captures. Treat these as reference material, not application source.
- `plans/` holds Atlas/Prometheus plan and completion notes. Continue to put those plan files there.

Keep API contracts in `Program.cs`/`Controllers/`, TypeScript types in `frontend/src/types/api.ts`, and callers in sync. Keep EF entities, `PrisstyrningDbContext`, repositories, migrations, and persistence tests aligned when the schema changes.

## Setup, build, and run

Prerequisites evidenced by CI and manifests are the .NET 10 SDK, Node.js 20 with npm, and PostgreSQL for a running application. Docker is optional.

From the repository root:

```text
dotnet restore
cd frontend
npm ci
```

Development commands:

```text
dotnet run --configuration Release
cd frontend && npm run dev
```

The backend defaults to port 5000 and the Vite server to port 5173; Vite proxies `/api` and `/auth` to the backend. Backend startup applies EF Core migrations and therefore requires a reachable PostgreSQL database configured through `ConnectionStrings:DefaultConnection` (environment variables may use the `PRISSTYRNING_` prefix).

Build and test commands matching CI:

```text
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --verbosity normal
cd frontend && npm run build
```

For an individual backend test:

```text
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

The frontend `build` script runs TypeScript checking (`tsc`) before Vite. `npm test` runs Vitest and `npm run test:e2e` runs the Playwright suite after a production build. There is no frontend lint or format script and no repository-wide formatter configuration.

Optional container commands documented by the repository are:

```text
docker build -t prisstyrning:local .
docker compose -f docker-compose.example.yml up -d
```

The Compose command starts persistent services and the container build can require network access; run them only when the task calls for container/runtime validation.

## Verification sequence

Inspect the relevant implementation and tests before editing. Preserve public behavior and API payloads unless the task requests a change, keep the patch focused, and do not overwrite unrelated user changes.

Use proportionate verification:

1. For backend-only changes, run the narrowest relevant `dotnet test --filter ...`, then `dotnet build --configuration Release --no-restore` and the full backend test command when warranted.
2. For frontend-only changes, run `cd frontend && npm run build`; manually exercise the affected route when runtime/UI behavior changed.
3. For cross-stack or release-sensitive changes, run the backend Release build and tests followed by the frontend build, mirroring PR CI.
4. Add a regression test for bug fixes when practical. Follow the existing xUnit style: descriptive `Method_Scenario_ExpectedResult` names, Arrange/Act/Assert structure, isolated in-memory EF databases, and mocked HTTP clients.

Report every command run, its outcome, skipped checks, and remaining uncertainty. Clearly distinguish repository evidence from assumptions. If a check fails because of the environment, report that separately from product failures.

## Coding, data, and security constraints

- C# uses nullable reference types, implicit usings, file-scoped namespaces, four-space indentation, async methods with `Async` suffixes, and `DateTimeOffset`/UTC for persisted instants.
- Frontend conventions are scoped in `frontend/AGENTS.md`.
- Do not commit credentials, OAuth tokens, cookies, connection-string secrets, or local environment overrides. `tokens/`, `.env`, `appsettings.Local.json`, and development appsettings files are ignored for this reason. Keep automatic schedule application disabled unless a task explicitly changes that behavior.
- Database migrations run automatically at application startup. Generate schema changes with EF tooling and review them carefully; do not hand-edit `*.Designer.cs` or `PrisstyrningDbContextModelSnapshot.cs`. The exact migration-generation command is not established as a supported repository command because no local tool manifest is checked in.
- Preserve migration and API compatibility unless a task explicitly introduces a breaking change; document intentional breaking changes in `MIGRATION.md`.
- Do not edit generated/dependency outputs: `bin/`, `obj/`, `frontend/node_modules/`, `frontend/.vite/`, or `wwwroot/`. `wwwroot/` is replaced by `npm run build` from `frontend/`.
- Runtime data under `data/nordpool/`, `data/schedule_history/`, `data/tokens*/`, `data/migration-backup/`, and root `tokens/` is not source code. Do not modify or commit it.

## Repository automation and Codex notes

- `.github/workflows/pr-build-verification.yml` restores, builds, tests with coverage, builds the frontend, and verifies artifacts on pull requests to `master`.
- `.github/workflows/container.yml` builds and pushes a `linux/amd64` image to GHCR on `master`, version tags, and manual dispatch, and uploads a .NET publish artifact on `master`.
- `.github/workflows/block-pr-if-copilot-reviewing.yml` waits for Copilot review state. `.github/prompts/resolve-pr-comments.prompt.md` is an existing GitHub review workflow prompt.
- No repo-local Codex skills, hooks, MCP configuration, or automation configuration were found.
- Historical repo-local agent definitions and their installer have been removed; do not assume Atlas/Prometheus agents are available. The `plans/` convention remains in use for existing planning documents.
