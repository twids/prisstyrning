import { useState } from 'react';
import { Stack, Paper, Typography, Button, Alert, Box, Snackbar, Alert as MuiAlert, TextField } from '@mui/material';
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
  const [deviceIds, setDeviceIds] = useState({ gateway: '', embedded: '' });

  // New hooks
  const applySchedule = useApplySchedule();
  const currentSchedule = useCurrentSchedule();

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

    if (!deviceIds.gateway || !deviceIds.embedded) {
      setSnackbar({
        open: true,
        message: 'Gateway Device ID and Embedded ID are required',
        severity: 'error',
      });
      return;
    }

    try {
      await applySchedule.mutateAsync({
        gatewayDeviceId: deviceIds.gateway,
        embeddedId: deviceIds.embedded,
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
      await currentSchedule.mutateAsync(deviceIds.embedded || undefined);
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

      {/* Apply Schedule Section */}
      <Paper sx={{ p: 3 }}>
        <Typography variant="h5" gutterBottom>
          Apply Schedule to Daikin
        </Typography>

        <Stack spacing={2}>
          <TextField
            label="Gateway Device ID"
            value={deviceIds.gateway}
            onChange={(e) => setDeviceIds({ ...deviceIds, gateway: e.target.value })}
            placeholder="Enter gateway device ID"
            fullWidth
            disabled={!isAuthorized}
          />
          
          <TextField
            label="Embedded ID"
            value={deviceIds.embedded}
            onChange={(e) => setDeviceIds({ ...deviceIds, embedded: e.target.value })}
            placeholder="Enter embedded ID"
            fullWidth
            disabled={!isAuthorized}
          />

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
        <MuiAlert
          onClose={() => setSnackbar({ ...snackbar, open: false })}
          severity={snackbar.severity}
          sx={{ width: '100%' }}
        >
          {snackbar.message}
        </MuiAlert>
      </Snackbar>
    </Stack>
  );
}
