import { useState, useEffect } from 'react';
import {
  Container,
  Paper,
  Typography,
  Stack,
  Slider,
  TextField,
  Switch,
  FormControlLabel,
  Button,
  Divider,
  Box,
  Alert,
  Snackbar,
  Select,
  MenuItem,
  FormControl,
  InputLabel,
  CircularProgress,
} from '@mui/material';
import { useUserSettings } from '../hooks/useUserSettings';
import { useZone } from '../hooks/useZone';
import { useAuth } from '../hooks/useAuth';
import ConfirmDialog from '../components/ConfirmDialog';
import LoadingSkeleton from '../components/LoadingSkeleton';

// Nordpool zones
const ZONES = [
  'SE1', 'SE2', 'SE3', 'SE4',
  'NO1', 'NO2', 'NO3', 'NO4', 'NO5',
  'DK1', 'DK2',
  'FI',
];

export default function SettingsPage() {
  const { settings, isLoading, error, updateSettings, isUpdating } = useUserSettings();
  const { zone, setZone, isUpdating: isZoneUpdating } = useZone();
  const { isAuthorized, refresh, revoke, isRefreshing } = useAuth();

  // Local state for form
  const [formData, setFormData] = useState({
    comfortHours: 3,
    turnOffPercentile: 0.9,
    maxComfortGapHours: 1,
    autoApplySchedule: false,
    selectedZone: 'SE3',
    schedulingMode: 'Classic' as 'Classic' | 'Flexible',
    ecoIntervalHours: 24,
    ecoFlexibilityHours: 12,
    comfortIntervalDays: 21,
    comfortFlexibilityDays: 7,
    comfortEarlyPercentile: 0.10,
  });

  const [snackbar, setSnackbar] = useState({
    open: false,
    message: '',
    severity: 'success' as 'success' | 'error' | 'info',
  });

  const [revokeDialog, setRevokeDialog] = useState(false);

  // Initialize form from settings
  useEffect(() => {
    if (settings) {
      setFormData(prev => ({
        ...prev,
        comfortHours: settings.ComfortHours ?? 3,
        turnOffPercentile: settings.TurnOffPercentile ?? 0.9,
        maxComfortGapHours: settings.MaxComfortGapHours ?? 1,
        autoApplySchedule: settings.AutoApplySchedule ?? false,
        schedulingMode: settings.SchedulingMode ?? 'Classic',
        ecoIntervalHours: settings.EcoIntervalHours ?? 24,
        ecoFlexibilityHours: settings.EcoFlexibilityHours ?? 12,
        comfortIntervalDays: settings.ComfortIntervalDays ?? 21,
        comfortFlexibilityDays: settings.ComfortFlexibilityDays ?? 7,
        comfortEarlyPercentile: settings.ComfortEarlyPercentile ?? 0.10,
      }));
    }
  }, [settings]);

  useEffect(() => {
    if (zone) {
      setFormData(prev => ({
        ...prev,
        selectedZone: zone
      }));
    }
  }, [zone]);


  const handleSaveSettings = async () => {
    try {
      await updateSettings({
        ComfortHours: formData.comfortHours,
        TurnOffPercentile: formData.turnOffPercentile,
        MaxComfortGapHours: formData.maxComfortGapHours,
        AutoApplySchedule: formData.autoApplySchedule,
        SchedulingMode: formData.schedulingMode,
        EcoIntervalHours: formData.ecoIntervalHours,
        EcoFlexibilityHours: formData.ecoFlexibilityHours,
        ComfortIntervalDays: formData.comfortIntervalDays,
        ComfortFlexibilityDays: formData.comfortFlexibilityDays,
        ComfortEarlyPercentile: formData.comfortEarlyPercentile,
      });

      if (formData.selectedZone !== zone) {
        await setZone(formData.selectedZone);
      }

      setSnackbar({
        open: true,
        message: 'Settings saved successfully',
        severity: 'success',
      });
    } catch (err) {
      setSnackbar({
        open: true,
        message: `Failed to save settings: ${err}`,
        severity: 'error',
      });
    }
  };

  const handleRevokeToken = async () => {
    setRevokeDialog(false);
    try {
      await revoke();
      setSnackbar({
        open: true,
        message: 'Daikin authentication revoked',
        severity: 'info',
      });
    } catch (err) {
      setSnackbar({
        open: true,
        message: `Failed to revoke: ${err}`,
        severity: 'error',
      });
    }
  };

  if (isLoading) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <LoadingSkeleton />
      </Container>
    );
  }

  if (error) {
    return (
      <Container maxWidth="md" sx={{ py: 4 }}>
        <Alert severity="error">Failed to load settings: {error.message}</Alert>
      </Container>
    );
  }

  return (
    <Container maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h4" gutterBottom sx={{ fontSize: { xs: '1.5rem', md: '2rem' } }}>
        Settings
      </Typography>

      <Stack spacing={3}>
        {/* Schedule Settings */}
        <Paper sx={{ p: 3 }}>
          <Typography variant="h6" gutterBottom>
            Schedule Configuration
          </Typography>

          <Stack spacing={3}>
            <Box>
              <Typography gutterBottom>
                Comfort Hours: {formData.comfortHours}
              </Typography>
              <Slider
                value={formData.comfortHours}
                onChange={(_, value) =>
                  setFormData({ ...formData, comfortHours: value as number })
                }
                min={1}
                max={12}
                step={1}
                marks
                valueLabelDisplay="auto"
              />
              <Typography variant="caption" color="text.secondary">
                Number of hours per day to heat water to comfort temperature
              </Typography>
            </Box>

            <Box>
              <Typography gutterBottom>
                Turn Off Percentile: {(formData.turnOffPercentile * 100).toFixed(0)}%
              </Typography>
              <Slider
                value={formData.turnOffPercentile}
                onChange={(_, value) =>
                  setFormData({ ...formData, turnOffPercentile: value as number })
                }
                min={0.5}
                max={0.99}
                step={0.01}
                marks={[
                  { value: 0.5, label: '50%' },
                  { value: 0.75, label: '75%' },
                  { value: 0.99, label: '99%' },
                ]}
                valueLabelDisplay="auto"
                valueLabelFormat={(value) => `${(value * 100).toFixed(0)}%`}
              />
              <Typography variant="caption" color="text.secondary">
                Price threshold for turning off DHW heating (higher = less turn-off)
              </Typography>
            </Box>

            <TextField
              label="Max Comfort Gap (hours)"
              type="number"
              value={formData.maxComfortGapHours}
              onChange={(e) =>
                setFormData({
                  ...formData,
                  maxComfortGapHours: parseInt(e.target.value) || 1,
                })
              }
              inputProps={{ min: 1, max: 72, step: 1 }}
              helperText="Maximum gap between consecutive comfort hours (1-72)"
            />

            <FormControl fullWidth>
              <InputLabel>Nordpool Zone</InputLabel>
              <Select
                value={formData.selectedZone}
                onChange={(e) =>
                  setFormData({ ...formData, selectedZone: e.target.value })
                }
                label="Nordpool Zone"
              >
                {ZONES.map((z) => (
                  <MenuItem key={z} value={z}>
                    {z}
                  </MenuItem>
                ))}
              </Select>
            </FormControl>
          </Stack>
        </Paper>

        {/* Scheduling Mode */}
        <Paper sx={{ p: 3 }}>
          <Typography variant="h6" gutterBottom>
            Scheduling Mode
          </Typography>

          <Stack spacing={2}>
            <FormControl fullWidth>
              <InputLabel>Mode</InputLabel>
              <Select
                value={formData.schedulingMode}
                onChange={(e) => setFormData({ ...formData, schedulingMode: e.target.value as 'Classic' | 'Flexible' })}
                label="Mode"
              >
                <MenuItem value="Classic">Classic (Fixed daily schedule)</MenuItem>
                <MenuItem value="Flexible">Flexible (Interval-based with price optimization)</MenuItem>
              </Select>
            </FormControl>

            <Typography variant="caption" color="text.secondary">
              Classic mode generates a fixed daily schedule. Flexible mode schedules eco and comfort runs at optimal prices within configurable intervals.
            </Typography>
          </Stack>
        </Paper>

        {/* Flexible Scheduling Settings - only show when Flexible mode */}
        {formData.schedulingMode === 'Flexible' && (
          <Paper sx={{ p: 3 }}>
            <Typography variant="h6" gutterBottom>
              Flexible Schedule Settings
            </Typography>

            <Stack spacing={3}>
              {/* Eco Section */}
              <Typography variant="subtitle1" fontWeight="bold">Eco (Daily DHW ~45°C)</Typography>

              <Box>
                <Typography gutterBottom>
                  Eco Interval: {formData.ecoIntervalHours} hours
                </Typography>
                <Slider
                  value={formData.ecoIntervalHours}
                  onChange={(_, value) => setFormData({ ...formData, ecoIntervalHours: value as number })}
                  min={6}
                  max={36}
                  step={1}
                  marks={[
                    { value: 6, label: '6h' },
                    { value: 12, label: '12h' },
                    { value: 24, label: '24h' },
                    { value: 36, label: '36h' },
                  ]}
                  valueLabelDisplay="auto"
                />
                <Typography variant="caption" color="text.secondary">
                  How often eco heating should run (target interval)
                </Typography>
              </Box>

              <Box>
                <Typography gutterBottom>
                  Eco Flexibility: ±{formData.ecoFlexibilityHours} hours
                </Typography>
                <Slider
                  value={formData.ecoFlexibilityHours}
                  onChange={(_, value) => setFormData({ ...formData, ecoFlexibilityHours: value as number })}
                  min={1}
                  max={18}
                  step={1}
                  marks={[
                    { value: 1, label: '±1h' },
                    { value: 6, label: '±6h' },
                    { value: 12, label: '±12h' },
                    { value: 18, label: '±18h' },
                  ]}
                  valueLabelDisplay="auto"
                />
                <Typography variant="caption" color="text.secondary">
                  Scheduling window: eco runs between {Math.max(0, formData.ecoIntervalHours - formData.ecoFlexibilityHours)}h and {formData.ecoIntervalHours + formData.ecoFlexibilityHours}h after last run
                </Typography>
              </Box>

              <Divider />

              {/* Comfort Section */}
              <Typography variant="subtitle1" fontWeight="bold">Comfort (Legionella ~60°C)</Typography>

              <Box>
                <Typography gutterBottom>
                  Comfort Interval: {formData.comfortIntervalDays} days
                </Typography>
                <Slider
                  value={formData.comfortIntervalDays}
                  onChange={(_, value) => setFormData({ ...formData, comfortIntervalDays: value as number })}
                  min={7}
                  max={90}
                  step={1}
                  marks={[
                    { value: 7, label: '7d' },
                    { value: 21, label: '21d' },
                    { value: 30, label: '30d' },
                    { value: 60, label: '60d' },
                    { value: 90, label: '90d' },
                  ]}
                  valueLabelDisplay="auto"
                />
                <Typography variant="caption" color="text.secondary">
                  How often comfort (legionella) heating should run
                </Typography>
              </Box>

              <Box>
                <Typography gutterBottom>
                  Comfort Flexibility: ±{formData.comfortFlexibilityDays} days
                </Typography>
                <Slider
                  value={formData.comfortFlexibilityDays}
                  onChange={(_, value) => setFormData({ ...formData, comfortFlexibilityDays: value as number })}
                  min={1}
                  max={30}
                  step={1}
                  marks={[
                    { value: 1, label: '±1d' },
                    { value: 7, label: '±7d' },
                    { value: 14, label: '±14d' },
                    { value: 30, label: '±30d' },
                  ]}
                  valueLabelDisplay="auto"
                />
                <Typography variant="caption" color="text.secondary">
                  Scheduling window: comfort runs between {Math.max(0, formData.comfortIntervalDays - formData.comfortFlexibilityDays)}d and {formData.comfortIntervalDays + formData.comfortFlexibilityDays}d after last run
                </Typography>
              </Box>

              <Box>
                <Typography gutterBottom>
                  Early Comfort Threshold: {(formData.comfortEarlyPercentile * 100).toFixed(0)}th percentile
                </Typography>
                <Slider
                  value={formData.comfortEarlyPercentile}
                  onChange={(_, value) => setFormData({ ...formData, comfortEarlyPercentile: value as number })}
                  min={0.01}
                  max={0.50}
                  step={0.01}
                  marks={[
                    { value: 0.05, label: '5%' },
                    { value: 0.10, label: '10%' },
                    { value: 0.25, label: '25%' },
                    { value: 0.50, label: '50%' },
                  ]}
                  valueLabelDisplay="auto"
                  valueLabelFormat={(value) => `${(value * 100).toFixed(0)}%`}
                />
                <Typography variant="caption" color="text.secondary">
                  When the comfort window opens, only trigger if the price is below this historical percentile. The threshold relaxes as the window progresses.
                </Typography>
              </Box>
            </Stack>
          </Paper>
        )}

        {/* Automation Settings */}
        <Paper sx={{ p: 3 }}>
          <Typography variant="h6" gutterBottom>
            Automation
          </Typography>

          <FormControlLabel
            control={
              <Switch
                checked={formData.autoApplySchedule}
                onChange={(e) =>
                  setFormData({ ...formData, autoApplySchedule: e.target.checked })
                }
              />
            }
            label="Auto-apply schedule daily"
          />
          <Typography variant="caption" color="text.secondary" display="block">
            Automatically apply generated schedule to your Daikin device each day
          </Typography>
        </Paper>

        {/* Save Button */}
        <Button
          variant="contained"
          size="large"
          onClick={handleSaveSettings}
          disabled={isUpdating || isZoneUpdating}
        >
          {isUpdating || isZoneUpdating ? (
            <>
              <CircularProgress size={20} sx={{ mr: 1 }} />
              Saving...
            </>
          ) : (
            'Save Settings'
          )}
        </Button>

        <Divider />

        {/* Danger Zone */}
        <Paper sx={{ p: 3, border: '1px solid', borderColor: 'error.main' }}>
          <Typography variant="h6" color="error" gutterBottom>
            Danger Zone
          </Typography>

          <Stack spacing={2}>
            <Box>
              <Typography variant="body2" gutterBottom>
                Revoke Daikin authentication to disconnect your account.
              </Typography>
              <Button
                variant="outlined"
                color="error"
                onClick={() => setRevokeDialog(true)}
                disabled={!isAuthorized}
              >
                Revoke Daikin Access
              </Button>
            </Box>

            <Box>
              <Typography variant="body2" gutterBottom>
                Refresh authentication token (use if experiencing connectivity issues).
              </Typography>
              <Button
                variant="outlined"
                onClick={() => refresh()}
                disabled={!isAuthorized || isRefreshing}
              >
                {isRefreshing ? 'Refreshing...' : 'Refresh Token'}
              </Button>
            </Box>
          </Stack>
        </Paper>
      </Stack>

      {/* Dialogs */}
      <ConfirmDialog
        open={revokeDialog}
        title="Revoke Daikin Access"
        message="Are you sure you want to revoke access to your Daikin account? You will need to re-authorize to use schedule application features."
        confirmText="Revoke"
        cancelText="Cancel"
        onConfirm={handleRevokeToken}
        onCancel={() => setRevokeDialog(false)}
        isDestructive
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
        >
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Container>
  );
}
