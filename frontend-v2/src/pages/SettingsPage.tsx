import { useState, useEffect } from 'react';
import Card from '../components/Card';
import Slider from '../components/Slider';
import ConfirmDialog from '../components/ConfirmDialog';
import LoadingSkeleton from '../components/LoadingSkeleton';
import { useUserSettings } from '../hooks/useUserSettings';
import { useZone } from '../hooks/useZone';
import { useAuth } from '../hooks/useAuth';
import { useToast } from '../context/ToastContext';

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
  const { showToast } = useToast();

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
        selectedZone: zone,
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

      showToast('Settings saved successfully', 'success');
    } catch (err) {
      showToast(`Failed to save settings: ${err}`, 'error');
    }
  };

  const handleRevokeToken = async () => {
    setRevokeDialog(false);
    try {
      await revoke();
      showToast('Daikin authentication revoked', 'info');
    } catch (err) {
      showToast(`Failed to revoke: ${err}`, 'error');
    }
  };

  if (isLoading) {
    return <LoadingSkeleton />;
  }

  if (error) {
    return (
      <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-4 text-sm text-red-600 dark:text-red-400">
        Failed to load settings: {error.message}
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <h1 className="text-2xl md:text-3xl font-bold">Settings</h1>

      {/* Schedule Configuration */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Schedule Configuration</h2>
        <div className="space-y-5">
          <Slider
            label="Comfort Hours"
            value={formData.comfortHours}
            onChange={(v) => setFormData({ ...formData, comfortHours: v })}
            min={1}
            max={12}
            step={1}
            displayValue={`${formData.comfortHours}`}
            helpText="Number of hours per day to heat water to comfort temperature"
          />

          <Slider
            label="Turn Off Percentile"
            value={formData.turnOffPercentile}
            onChange={(v) => setFormData({ ...formData, turnOffPercentile: v })}
            min={0.5}
            max={0.99}
            step={0.01}
            displayValue={`${(formData.turnOffPercentile * 100).toFixed(0)}%`}
            helpText="Price threshold for turning off DHW heating (higher = less turn-off)"
          />

          <div>
            <label className="block text-sm font-medium mb-1">Max Comfort Gap (hours)</label>
            <input
              type="number"
              value={formData.maxComfortGapHours}
              onChange={(e) => setFormData({ ...formData, maxComfortGapHours: parseInt(e.target.value) || 1 })}
              min={1}
              max={72}
              step={1}
              className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors"
            />
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">Maximum gap between consecutive comfort hours (1-72)</p>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">Nordpool Zone</label>
            <select
              value={formData.selectedZone}
              onChange={(e) => setFormData({ ...formData, selectedZone: e.target.value })}
              className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors"
            >
              {ZONES.map((z) => (
                <option key={z} value={z}>{z}</option>
              ))}
            </select>
          </div>
        </div>
      </Card>

      {/* Scheduling Mode */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Scheduling Mode</h2>
        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium mb-1">Mode</label>
            <select
              value={formData.schedulingMode}
              onChange={(e) => setFormData({ ...formData, schedulingMode: e.target.value as 'Classic' | 'Flexible' })}
              className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors"
            >
              <option value="Classic">Classic (Fixed daily schedule)</option>
              <option value="Flexible">Flexible (Interval-based with price optimization)</option>
            </select>
          </div>
          <p className="text-xs text-gray-500 dark:text-gray-400">
            Classic mode generates a fixed daily schedule. Flexible mode schedules eco and comfort runs at optimal prices within configurable intervals.
          </p>
        </div>
      </Card>

      {/* Flexible Schedule Settings */}
      {formData.schedulingMode === 'Flexible' && (
        <Card>
          <h2 className="text-xl font-semibold mb-4">Flexible Schedule Settings</h2>
          <div className="space-y-5">
            {/* Eco Section */}
            <h3 className="font-semibold">Eco (Daily DHW ~45°C)</h3>

            <Slider
              label="Eco Interval"
              value={formData.ecoIntervalHours}
              onChange={(v) => setFormData({ ...formData, ecoIntervalHours: v })}
              min={6}
              max={36}
              step={1}
              displayValue={`${formData.ecoIntervalHours} hours`}
              helpText="How often eco heating should run (target interval)"
            />

            <Slider
              label="Eco Flexibility"
              value={formData.ecoFlexibilityHours}
              onChange={(v) => setFormData({ ...formData, ecoFlexibilityHours: v })}
              min={1}
              max={18}
              step={1}
              displayValue={`±${formData.ecoFlexibilityHours} hours`}
              helpText={`Scheduling window: eco runs between ${Math.max(0, formData.ecoIntervalHours - formData.ecoFlexibilityHours)}h and ${formData.ecoIntervalHours + formData.ecoFlexibilityHours}h after last run`}
            />

            <hr className="border-gray-200 dark:border-gray-800" />

            {/* Comfort Section */}
            <h3 className="font-semibold">Comfort (Legionella ~60°C)</h3>

            <Slider
              label="Comfort Interval"
              value={formData.comfortIntervalDays}
              onChange={(v) => setFormData({ ...formData, comfortIntervalDays: v })}
              min={7}
              max={90}
              step={1}
              displayValue={`${formData.comfortIntervalDays} days`}
              helpText="How often comfort (legionella) heating should run"
            />

            <Slider
              label="Comfort Flexibility"
              value={formData.comfortFlexibilityDays}
              onChange={(v) => setFormData({ ...formData, comfortFlexibilityDays: v })}
              min={1}
              max={30}
              step={1}
              displayValue={`±${formData.comfortFlexibilityDays} days`}
              helpText={`Scheduling window: comfort runs between ${Math.max(0, formData.comfortIntervalDays - formData.comfortFlexibilityDays)}d and ${formData.comfortIntervalDays + formData.comfortFlexibilityDays}d after last run`}
            />

            <Slider
              label="Early Comfort Threshold"
              value={formData.comfortEarlyPercentile}
              onChange={(v) => setFormData({ ...formData, comfortEarlyPercentile: v })}
              min={0.01}
              max={0.50}
              step={0.01}
              displayValue={`${(formData.comfortEarlyPercentile * 100).toFixed(0)}th percentile`}
              helpText="When the comfort window opens, only trigger if the price is below this historical percentile. The threshold relaxes as the window progresses."
            />
          </div>
        </Card>
      )}

      {/* Automation */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Automation</h2>
        <label className="flex items-center gap-3 cursor-pointer">
          <div className="relative">
            <input
              type="checkbox"
              checked={formData.autoApplySchedule}
              onChange={(e) => setFormData({ ...formData, autoApplySchedule: e.target.checked })}
              className="sr-only peer"
            />
            <div className="w-11 h-6 bg-gray-200 dark:bg-gray-700 peer-focus:ring-2 peer-focus:ring-blue-500 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-blue-600"></div>
          </div>
          <span className="text-sm font-medium">Auto-apply schedule daily</span>
        </label>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
          Automatically apply generated schedule to your Daikin device each day
        </p>
      </Card>

      {/* Save Button */}
      <button
        onClick={handleSaveSettings}
        disabled={isUpdating || isZoneUpdating}
        className="w-full px-6 py-3 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50"
      >
        {isUpdating || isZoneUpdating ? 'Saving...' : 'Save Settings'}
      </button>

      <hr className="border-gray-200 dark:border-gray-800" />

      {/* Danger Zone */}
      <Card className="border-red-300 dark:border-red-800">
        <h2 className="text-xl font-semibold text-red-600 dark:text-red-400 mb-4">Danger Zone</h2>
        <div className="space-y-4">
          <div>
            <p className="text-sm text-gray-600 dark:text-gray-400 mb-2">
              Revoke Daikin authentication to disconnect your account.
            </p>
            <button
              onClick={() => setRevokeDialog(true)}
              disabled={!isAuthorized}
              className="px-4 py-2 border border-red-300 dark:border-red-700 text-red-600 dark:text-red-400 text-sm font-medium rounded-lg hover:bg-red-50 dark:hover:bg-red-950 transition-colors disabled:opacity-50"
            >
              Revoke Daikin Access
            </button>
          </div>
          <div>
            <p className="text-sm text-gray-600 dark:text-gray-400 mb-2">
              Refresh authentication token (use if experiencing connectivity issues).
            </p>
            <button
              onClick={() => refresh()}
              disabled={!isAuthorized || isRefreshing}
              className="px-4 py-2 border border-gray-300 dark:border-gray-700 text-sm font-medium rounded-lg hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors disabled:opacity-50"
            >
              {isRefreshing ? 'Refreshing...' : 'Refresh Token'}
            </button>
          </div>
        </div>
      </Card>

      {/* Revoke Confirm Dialog */}
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
    </div>
  );
}
