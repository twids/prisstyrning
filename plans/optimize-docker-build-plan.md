## Plan: Optimize Docker Build Performance

Speed up the GitHub Actions Docker build by removing unnecessary multi-arch (arm64) builds and adding BuildKit cache mounts for package managers.

**Phases (2 phases)**

1. **Phase 1: Add BuildKit cache mounts to Dockerfile**
    - **Objective:** Use `--mount=type=cache` for npm and NuGet caches so package restores are near-instant on warm caches, even when Docker layer cache misses.
    - **Files/Functions to Modify/Create:** `Dockerfile`
    - **Tests to Write:** None (infrastructure change, validated by successful build)
    - **Steps:**
        1. Add `# syntax=docker/dockerfile:1` directive for explicit BuildKit support
        2. Add `--mount=type=cache,target=/root/.npm` to both `npm ci` RUN commands (frontend-build and frontend-v2-build stages)
        3. Add `--mount=type=cache,target=/root/.nuget/packages` to the `dotnet restore` RUN command (backend-build stage)

2. **Phase 2: Remove multi-arch and simplify workflow**
    - **Objective:** Remove QEMU emulation and arm64 builds since only amd64 is needed. This eliminates the biggest bottleneck (arm64 under QEMU is 10-20x slower than native).
    - **Files/Functions to Modify/Create:** `.github/workflows/container.yml`
    - **Tests to Write:** None (infrastructure change, validated by successful workflow run)
    - **Steps:**
        1. Remove QEMU setup step (no longer needed)
        2. Remove unnecessary Buildx driver options
        3. Change `platforms` from `linux/amd64,linux/arm64` to `linux/amd64`
        4. Upgrade `docker/build-push-action` from v5 to v6

**Open Questions**
1. None — amd64-only confirmed by repo owner.
