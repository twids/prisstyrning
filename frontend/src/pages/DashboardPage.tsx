import { useState } from 'react';
import { Stack, Paper, Typography, Button, Alert, Box, Snackbar, TextField, Divider } from '@mui/material';

import AuthStatusChip from '../components/AuthStatusChip';
import PriceChart from '../components/PriceChart';
import ScheduleGrid from '../components/ScheduleGrid';
import ScheduleLegend from '../components/ScheduleLegend';
import ScheduleHistoryList from '../components/ScheduleHistoryList';
import JsonViewer from '../components/JsonViewer';
import ConfirmDialog from '../components/ConfirmDialog';
import { useAuth } from '../hooks/useAuth';
import { useSchedulePreview } from '../hooks/useSchedulePreview';
import { useApplySchedule } from '../hooks/useApplySchedule';
import { useCurrentSchedule } from '../hooks/useCurrentSchedule';
import { useFlexibleState } from '../hooks/useFlexibleState';
import { useUserSettings } from '../hooks/useUserSettings';
import { useManualComfort } from '../hooks/useManualComfort';

export default function DashboardPage() {
  const { isAuthorized, startAuth, refresh, isRefreshing } = useAuth();
  const schedulePreview = useSchedulePreview();

  // New state
  const [snackbar, setSnackbar] = useState<{
    open: boolean;
    message: string;
    severity: 'success' | 'error' | 'info' | 'warning';
  }>({ open: false, message: '', severity: 'info' });

  const [applyDialog, setApplyDialog] = useState(false);

  // New hooks
  const applySchedule = useApplySchedule();
  const currentSchedule = useCurrentSchedule();
  const { settings } = useUserSettings();
  const isFlexible = settings?.SchedulingMode === 'Flexible';
  const { state: flexibleState } = useFlexibleState(isFlexible);
  const manualComfort = useManualComfort();

  const formatDateTimeLocal = (date: Date): string => {
    const pad = (n: number) => n.toString().padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  };

  const [manualComfortTime, setManualComfortTime] = useState(() => {
    const nextHour = new Date();
    nextHour.setHours(nextHour.getHours() + 1, 0, 0, 0);
    return formatDateTimeLocal(nextHour);
  });

  const handleManualComfort = async () => {
    if (!manualComfortTime) return;
    try {
      const comfortDate = new Date(manualComfortTime);
      const result = await manualComfort.mutateAsync(comfortDate.toISOString());
      setSnackbar({
        open: true,
        message: result.message,
        severity: result.applied ? 'success' : 'warning',
      });
    } catch (error) {
      setSnackbar({
        open: true,
        message: `Failed to schedule comfort: ${error}`,
        severity: 'error',
      });
    }
  };

  const handleGenerateSchedule = async () => {
    try {
      await schedulePreview.mutateAsync();
    } catch (error) {
      console.error('Failed to generate schedule:', error);
    }
  };

  const handleApplySchedule = async () => {
    if (!schedulePreview.data?.schedulePayload) {
      setSnackbar({
        open: true,
        message: 'No schedule to apply. Generate a schedule first.',
        severity: 'error',
      });
      return;
    }

    setApplyDialog(true);
  };

  const confirmApplySchedule = async () => {
    setApplyDialog(false);

    try {
      // Device IDs will be auto-detected by the backend
      await applySchedule.mutateAsync({
        schedulePayload: schedulePreview.data!.schedulePayload!,
      });

      setSnackbar({
        open: true,
        message: 'Schedule applied successfully!',
        severity: 'success',
      });
    } catch (error) {
      setSnackbar({
        open: true,
        message: `Failed to apply schedule: ${error}`,
        severity: 'error',
      });
    }
  };

  const handleRetrieveCurrentSchedule = async () => {
    try {
      // embeddedId will be auto-detected by the backend
      await currentSchedule.mutateAsync(undefined);
      setSnackbar({
        open: true,
        message: 'Current schedule retrieved',
        severity: 'success',
      });
    } catch (error) {
      setSnackbar({
        open: true,
        message: `Failed to retrieve schedule: ${error}`,
        severity: 'error',
      });
    }
  };

  return (
    <Stack spacing={3}>
      {/* Auth Section */}
      <Paper sx={{ p: 3 }}>
        <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 2 }}>
          <Typography variant="h5">Daikin Authorization</Typography>
          <AuthStatusChip />
        </Stack>
        
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          {!isAuthorized ? (
            <Button variant="contained" onClick={startAuth}>
              Start OAuth Flow
            </Button>
          ) : (
            <Button
              variant="outlined"
              onClick={() => refresh()}
              disabled={isRefreshing}
            >
              {isRefreshing ? 'Refreshing...' : 'Refresh Token'}
            </Button>
          )}
        </Stack>
      </Paper>

      {/* Price Chart */}
      <PriceChart />

      {/* Schedule Preview */}
      <Paper sx={{ p: 3 }}>
        <Typography variant="h5" gutterBottom>
          Schedule Preview
        </Typography>

        {!isAuthorized && (
          <Alert severity="warning" sx={{ mb: 2 }}>
            Authorize with Daikin to apply schedules to your device
          </Alert>
        )}

        <Button
          variant="contained"
          color="primary"
          onClick={handleGenerateSchedule}
          disabled={schedulePreview.isPending}
          sx={{ mb: 2 }}
        >
          {schedulePreview.isPending ? 'Generating...' : 'Generate Schedule'}
        </Button>

        {schedulePreview.isError && (
          <Alert severity="error" sx={{ mb: 2 }}>
            Failed to generate schedule: {schedulePreview.error.message}
          </Alert>
        )}

        {schedulePreview.data && (
          <Box>
            <ScheduleGrid schedulePayload={schedulePreview.data.schedulePayload} />
            <ScheduleLegend />
            
            {schedulePreview.data.message && (
              <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>
                {schedulePreview.data.message}
              </Typography>
            )}
          </Box>
        )}
      </Paper>

      {/* Manual Comfort Run */}
      <Paper sx={{ p: 3 }}>
        <Typography variant="h5" gutterBottom>
          Manual Comfort Run
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Schedule an immediate comfort run (e.g., for filling a hot tub). Select a time within the next 48 hours.
        </Typography>

        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems="flex-end">
          <TextField
            type="datetime-local"
            label="Comfort Time"
            value={manualComfortTime}
            onChange={(e) => setManualComfortTime(e.target.value)}
            InputLabelProps={{ shrink: true }}
            inputProps={{
              min: formatDateTimeLocal(new Date()),
              max: formatDateTimeLocal(new Date(Date.now() + 48 * 60 * 60 * 1000)),
            }}
            fullWidth
          />
          <Button
            variant="contained"
            onClick={handleManualComfort}
            disabled={!isAuthorized || !manualComfortTime || manualComfort.isPending}
            sx={{ whiteSpace: 'nowrap', minWidth: 160 }}
          >
            {manualComfort.isPending ? 'Scheduling...' : 'Schedule & Apply'}
          </Button>
        </Stack>

        {!isAuthorized && (
          <Alert severity="warning" sx={{ mt: 2 }}>
            Authorize with Daikin before scheduling a manual comfort run.
          </Alert>
        )}
      </Paper>

      {/* Flexible Scheduling Status */}
      {isFlexible && flexibleState && (
        <Paper sx={{ p: 3 }}>
          <Typography variant="h5" gutterBottom>
            Flexible Scheduling Status
          </Typography>

          <Stack spacing={2}>
            {/* Eco Status */}
            <Box>
              <Typography variant="subtitle1" fontWeight="bold">
                Eco (Daily DHW)
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Last scheduled: {flexibleState.LastEcoRunUtc
                  ? new Date(flexibleState.LastEcoRunUtc).toLocaleString()
                  : 'Never (waiting for first interval)'}
              </Typography>
              {flexibleState.EcoWindow.Start && flexibleState.EcoWindow.End && (
                <Typography variant="body2" color="text.secondary">
                  Next window: {new Date(flexibleState.EcoWindow.Start).toLocaleString()} – {new Date(flexibleState.EcoWindow.End).toLocaleString()}
                </Typography>
              )}
            </Box>

            <Divider />

            {/* Comfort Status */}
            <Box>
              <Typography variant="subtitle1" fontWeight="bold">
                Comfort (Legionella)
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Last run: {flexibleState.LastComfortRunUtc
                  ? new Date(flexibleState.LastComfortRunUtc).toLocaleString()
                  : 'Never (waiting for first interval)'}
              </Typography>
              {flexibleState.NextScheduledComfortUtc && (
                <Typography variant="body2" color="primary.main">
                  Next scheduled: {new Date(flexibleState.NextScheduledComfortUtc).toLocaleString()}
                </Typography>
              )}
              {flexibleState.ComfortWindow.Start && flexibleState.ComfortWindow.End && (
                <>
                  <Typography variant="body2" color="text.secondary">
                    Window: {new Date(flexibleState.ComfortWindow.Start).toLocaleString()} – {new Date(flexibleState.ComfortWindow.End).toLocaleString()}
                  </Typography>
                  {flexibleState.ComfortWindow.Progress !== null && (
                    <Box sx={{ mt: 1 }}>
                      <Typography variant="caption" color="text.secondary">
                        Window progress: {(flexibleState.ComfortWindow.Progress * 100).toFixed(0)}%
                      </Typography>
                      <Box
                        sx={{
                          mt: 0.5,
                          height: 8,
                          borderRadius: 4,
                          bgcolor: 'grey.200',
                          overflow: 'hidden',
                        }}
                      >
                        <Box
                          sx={{
                            height: '100%',
                            width: `${(flexibleState.ComfortWindow.Progress ?? 0) * 100}%`,
                            bgcolor: (flexibleState.ComfortWindow.Progress ?? 0) > 0.9
                              ? 'warning.main'
                              : 'primary.main',
                            borderRadius: 4,
                            transition: 'width 0.3s ease',
                          }}
                        />
                      </Box>
                    </Box>
                  )}
                </>
              )}
            </Box>
          </Stack>
        </Paper>
      )}

      {/* Apply Schedule Section */}
      <Paper sx={{ p: 3 }}>
        <Typography variant="h5" gutterBottom>
          Apply Schedule to Daikin
        </Typography>

        <Alert severity="info" sx={{ mb: 2 }}>
          Device IDs will be automatically detected from your Daikin account.
        </Alert>

        <Stack spacing={2}>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
            <Button
              variant="contained"
              onClick={handleApplySchedule}
              disabled={!isAuthorized || !schedulePreview.data?.schedulePayload || applySchedule.isPending}
            >
              {applySchedule.isPending ? 'Applying...' : 'Apply Schedule'}
            </Button>

            <Button
              variant="outlined"
              onClick={handleRetrieveCurrentSchedule}
              disabled={!isAuthorized || currentSchedule.isPending}
            >
              {currentSchedule.isPending ? 'Retrieving...' : 'Retrieve Current Schedule'}
            </Button>
          </Stack>
        </Stack>

        {!!currentSchedule.data && (
          <Box sx={{ mt: 3 }}>
            <Typography variant="h6" gutterBottom>
              Current Schedule
            </Typography>
            <JsonViewer data={currentSchedule.data} />
          </Box>
        )}
      </Paper>

      {/* History Section */}
      <Paper sx={{ p: 3 }}>
        <Typography variant="h5" gutterBottom>
          Schedule History
        </Typography>
        <ScheduleHistoryList />
      </Paper>

      {/* Dialogs and Snackbar */}
      <ConfirmDialog
        open={applyDialog}
        title="Apply Schedule"
        message="Are you sure you want to apply this schedule to your Daikin device? This will replace the current schedule."
        confirmText="Apply"
        cancelText="Cancel"
        onConfirm={confirmApplySchedule}
        onCancel={() => setApplyDialog(false)}
      />

      <Snackbar
        open={snackbar.open}
        autoHideDuration={6000}
        onClose={() => setSnackbar({ ...snackbar, open: false })}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
      >
        <Alert
          onClose={() => setSnackbar({ ...snackbar, open: false })}
          severity={snackbar.severity}
          sx={{ width: '100%' }}
        >
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Stack>
  );
}
