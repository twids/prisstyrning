## Plan: Optimize Docker Build Performance

Speed up the GitHub Actions Docker build by eliminating QEMU arm64 emulation through parallel native runners and adding BuildKit cache mounts for package managers.

**Phases (2 phases)**

1. **Phase 1: Add BuildKit cache mounts to Dockerfile**
    - **Objective:** Use `--mount=type=cache` for npm and NuGet caches so package restores are near-instant on warm caches, even when Docker layer cache misses.
    - **Files/Functions to Modify/Create:** `Dockerfile`
    - **Tests to Write:** None (infrastructure change, validated by successful build)
    - **Steps:**
        1. Add `--mount=type=cache,target=/root/.npm` to both `npm ci` RUN commands (frontend-build and frontend-v2-build stages)
        2. Add `--mount=type=cache,target=/root/.nuget/packages` to the `dotnet restore` RUN command (backend-build stage)

2. **Phase 2: Split multi-arch into parallel native runners**
    - **Objective:** Eliminate QEMU arm64 emulation by building each architecture on a native runner in parallel, then merging manifests. This is the single biggest performance win (arm64 under QEMU is 10-20x slower than native).
    - **Files/Functions to Modify/Create:** `.github/workflows/container.yml`
    - **Tests to Write:** None (infrastructure change, validated by successful workflow run)
    - **Steps:**
        1. Create a `build` job with matrix strategy: `platform: [linux/amd64, linux/arm64]` mapped to `runner: [ubuntu-latest, ubuntu-24.04-arm]`
        2. Each matrix job builds and pushes a single-platform image by digest (no tags)
        3. Remove QEMU setup step (no longer needed for native builds)
        4. Add a `merge` job (depends on `build`) that uses `docker/metadata-action` for tags and `docker buildx imagetools create` to combine per-platform digests into a multi-arch manifest
        5. Move dotnet publish artifact step to a separate condition or keep on amd64 only
        6. Upgrade `docker/build-push-action` to v6

**Open Questions**
1. None — public repo confirmed, arm64 runners are available.
