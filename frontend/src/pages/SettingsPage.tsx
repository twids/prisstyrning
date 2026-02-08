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
                max={10}
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
                max={1.0}
                step={0.05}
                marks={[
                  { value: 0.5, label: '50%' },
                  { value: 0.75, label: '75%' },
                  { value: 1.0, label: '100%' },
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
              inputProps={{ min: 0, max: 5, step: 1 }}
              helperText="Maximum gap between consecutive comfort hours (0 = disabled)"
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
