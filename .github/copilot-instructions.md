# GitHub Copilot instructions

Follow the root `AGENTS.md` and any nested `AGENTS.md` applicable to the files being changed. Those files are the canonical source for project architecture, commands, conventions, generated-file boundaries, security constraints, and verification expectations.

## GitHub-specific context

- Pull requests target `master`.
- `.github/workflows/pr-build-verification.yml` is the authoritative PR validation sequence: restore NuGet packages, build the .NET solution in Release mode, run backend tests with coverage, install frontend dependencies with `npm ci`, run `npm run build`, and verify backend/frontend artifacts.
- `.github/workflows/container.yml` publishes the `linux/amd64` container to GHCR on pushes to `master`, version tags matching `v*.*.*`, and manual dispatch. It also uploads a .NET publish artifact for `master`.
- Keep pull requests focused. Add regression tests for bug fixes when practical, disclose skipped or environment-blocked checks, and do not claim validation that was not run.

Do not rely on historical instructions describing a vanilla JavaScript frontend, file-based persistence, ECO removal, fixed test counts, multi-architecture images, or repo-local Atlas/Prometheus agents; those descriptions no longer match the repository.
