# Migration Guide

This document describes breaking changes and migration steps for upgrading Prisstyrning.

## Version 2.0 - ECO Mode Removal & Frontend Rewrite

**Release Date:** TBD  
**GitHub Issue:** [#53](https://github.com/twids/prisstyrning/issues/53)

### Breaking Changes

#### 1. ECO Mode Removed (2-Mode System)

**Previous Behavior (3 modes):**
- **comfort**: Full DHW heating to comfort temperature
- **eco**: Reduced heating to eco-save temperature  
- **turn_off**: DHW heating completely disabled

**New Behavior (2 modes):**
- **comfort**: Full DHW heating to comfort temperature
- **turn_off**: DHW heating completely disabled

**Reason for Change:**  
ECO mode triggered unwanted heating cycles when transitioning from OFF→ECO, violating the intent of price-based optimization. The simplified 2-mode system provides clearer control: heat fully during cheap hours, turn off during expensive hours.

**Migration Impact:**

- **API Changes:**
  - `GET /api/schedule/preview` no longer returns schedules with `eco` state
  - Schedule JSON payloads only contain `comfort` and `turn_off` states
  - `TurnOffMaxConsecutive` setting **removed** (no longer applicable)

- **Configuration Changes:**
  - Remove `Settings:Schedule:TurnOffMaxConsecutive` from `appsettings.json` if present
  - Existing schedules with `eco` states will be rejected by Daikin API

- **Database/Storage:**
  - Historical schedules stored in `data/schedule_history/` may contain `eco` states
  - These remain readable but cannot be re-applied to devices
  - History retention policy (30 days) will naturally phase out old schedules

**Action Required:**

1. **Configuration Cleanup** (Optional):
   ```json
   // appsettings.json - REMOVE this section if present
   "Schedule": {
     "TurnOffMaxConsecutive": 4  // <-- DELETE THIS LINE
   }
   ```

2. **Re-generate Schedules:**
   - After upgrade, generate new schedules using the dashboard
   - New schedules will only use `comfort` and `turn_off` modes
   - Old schedules in history remain for reference

3. **Review Comfort Hours:**
   - With ECO removed, you may want to adjust `ComfortHours` setting
   - Previous typical values with ECO: 3-5 hours
   - Suggested values without ECO: 2-4 hours (system is more aggressive)

4. **Docker Users:**
   - Update image: `docker pull ghcr.io/twids/prisstyrning:latest`
   - No volume migration needed (config and history remain compatible)

#### 2. Frontend Complete Rewrite

**Previous:** Vanilla JavaScript with Chart.js  
**New:** React 18 + TypeScript + Material UI + Vite

**Migration Impact:**

- **No Action Required** (backward compatible)
- Static assets now built with Vite (see `frontend/` directory)
- Old `wwwroot/ui.js` replaced with chunked React bundles
- API endpoints unchanged (full backward compatibility)

**Benefits:**
- Modern responsive design with dark theme
- Real-time updates with React Query
- Type-safe API client (TypeScript)
- Improved UX with loading states and error handling

### Upgrade Instructions

#### Standard Upgrade (Docker)

```bash
# Pull latest image
docker pull ghcr.io/twids/prisstyrning:latest

# Stop existing container
docker stop prisstyrning

# Start new container (preserving data volume)
docker run -d \
  --name prisstyrning \
  --restart unless-stopped \
  -p 5000:5000 \
  -v ./data:/data \
  -e PRISSTYRNING_Daikin__ClientId=<your-client-id> \
  -e PRISSTYRNING_Daikin__ClientSecret=<your-client-secret> \
  ghcr.io/twids/prisstyrning:latest
```

#### Manual Upgrade (dotnet run)

```bash
# Pull latest code
git pull origin master

# Restore dependencies
dotnet restore
cd frontend && npm ci && cd ..

# Build backend + frontend
dotnet build --configuration Release
cd frontend && npm run build && cd ..

# Run application
dotnet run --configuration Release
```

#### Verification Steps

1. **Check Application Logs:**
   ```
   [NordpoolPriceJob] started
   [DaikinRefreshService] Started  
   Now listening on: http://[::]:5000
   ```

2. **Access Web Interface:**
   - Navigate to `http://localhost:5000`
   - Verify new Material UI design loads
   - Check authentication status chip in header

3. **Generate Test Schedule:**
   - Click "Generate Schedule Preview"
   - Verify grid shows only `comfort` (green) and `turn_off` (red)
   - Confirm no `eco` mode appears

4. **Review Settings:**
   - Navigate to Settings page (`/settings`)
   - Confirm `TurnOffMaxConsecutive` is not present
   - Adjust `ComfortHours` if needed (recommendation: reduce by 1 if using default 3)

### Rollback Procedure

If issues arise, rollback to previous version:

```bash
# Docker rollback (replace <previous-tag> with your last version)
docker pull ghcr.io/twids/prisstyrning:<previous-tag>
docker stop prisstyrning
docker run -d --name prisstyrning -p 5000:5000 -v ./data:/data \
  ghcr.io/twids/prisstyrning:<previous-tag>

# Git rollback (local installation)
git checkout <previous-commit-sha>
dotnet build --configuration Release
dotnet run --configuration Release
```

**Note:** Schedules generated with 2-mode system will not work on older versions expecting 3-mode format.

### Testing Recommendations

After upgrade, test the following workflows:

1. **Schedule Generation:**
   - Generate schedule preview
   - Verify response contains only `comfort` and `turn_off`
   - Check grid visualization matches data

2. **Schedule Application:**
   - Apply schedule to Daikin device
   - Confirm device accepts 2-mode schedule
   - Verify no "OFF→ECO unwanted heating" behavior

3. **History Retrieval:**
   - View schedule history
   - Expand historical entries
   - Old entries with `eco` display correctly but cannot be re-applied

4. **Settings Management:**
   - Update `ComfortHours` and `TurnOffPercentile`
   - Save settings and refresh preview
   - Confirm preview regenerates with new parameters

### FAQ

**Q: Will my historical schedules be deleted?**  
A: No. Historical schedules remain in `data/schedule_history/` for the configured retention period (default 30 days). However, old schedules with `eco` mode cannot be re-applied to devices.

**Q: Do I need to re-authorize with Daikin ONECTA?**  
A: No. OAuth tokens stored in `data/tokens/` remain valid. No re-authorization needed.

**Q: Can I still use the old frontend?**  
A: No. The vanilla JS frontend has been fully replaced. The new React frontend is backward-compatible with all API endpoints.

**Q: Should I adjust my ComfortHours setting after ECO removal?**  
A: Recommended. With ECO mode gone, the system is more efficient. If you were using 3-4 comfort hours with ECO, try reducing to 2-3 hours without ECO.

**Q: What happens to AutoApplySchedule with 2-mode system?**  
A: Auto-apply works unchanged. Daily job generates and applies new schedules using the 2-mode system.

**Q: Will Docker build sizes increase with React/TypeScript?**  
A: Yes, slightly. Node.js build stage adds ~100 MB to intermediate layers, but final runtime image increases by only ~5 MB (static assets). Multi-stage build keeps final image lean.

### Support

For issues or questions:
- **GitHub Issues:** https://github.com/twids/prisstyrning/issues
- **Discussions:** https://github.com/twids/prisstyrning/discussions

### Changelog Summary

**Added:**
- React 18 + TypeScript + Material UI frontend
- Modern responsive design with dark theme
- Code splitting for optimized bundle sizes
- LoadingSkeleton and ErrorBoundary components
- Comprehensive settings page with sliders and zone selector
- Schedule history timeline with expandable entries
- Real-time data updates with React Query

**Changed:**
- Schedule algorithm now generates 2-mode schedules (comfort/turn_off only)
- Default `HistoryRetentionDays` increased from 7 to 30
- Docker build now includes frontend compilation stage

**Removed:**
- ECO mode from schedule generation
- `TurnOffMaxConsecutive` configuration setting
- Vanilla JavaScript frontend (ui.js, ui.css)
- Chart.js dependency

**Fixed:**
- 7 critical bugs in ScheduleHistoryPersistence (JSON cloning, path traversal, concurrency, culture parsing)
- History persistence flag in preview endpoint (now persist: true)
- Silent failures in fire-and-forget history saves (added logging)

---

**Migration Guide Version:** 1.0  
**Last Updated:** 2025-01-30
