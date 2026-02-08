import { Stack, Paper, Typography, Button, Alert, Box } from '@mui/material';
import AuthStatusChip from '../components/AuthStatusChip';
import PriceChart from '../components/PriceChart';
import ScheduleGrid from '../components/ScheduleGrid';
import ScheduleLegend from '../components/ScheduleLegend';
import { useAuth } from '../hooks/useAuth';
import { useSchedulePreview } from '../hooks/useSchedulePreview';

export default function DashboardPage() {
  const { isAuthorized, startAuth, refresh, isRefreshing } = useAuth();
  const schedulePreview = useSchedulePreview();

  const handleGenerateSchedule = async () => {
    try {
      await schedulePreview.mutateAsync();
    } catch (error) {
      console.error('Failed to generate schedule:', error);
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
        
        <Stack direction="row" spacing={2}>
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
    </Stack>
  );
}
