import { useState } from 'react';
import Card from '../components/Card';
import AuthStatusBadge from '../components/AuthStatusBadge';
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
import { useToast } from '../context/ToastContext';

export default function DashboardPage() {
  const { isAuthorized, startAuth, refresh, isRefreshing } = useAuth();
  const schedulePreview = useSchedulePreview();
  const { showToast } = useToast();
  const [applyDialog, setApplyDialog] = useState(false);
  const [deviceIds, setDeviceIds] = useState({ gateway: '', embedded: '' });
  const applySchedule = useApplySchedule();
  const currentSchedule = useCurrentSchedule();
  const { settings } = useUserSettings();
  const isFlexible = settings?.SchedulingMode === 'Flexible';
  const { state: flexibleState } = useFlexibleState(isFlexible);
  const manualComfort = useManualComfort();

  const formatDateTimeLocal = (date: Date): string =>
    date.toISOString().slice(0, 16);

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
      showToast(result.message, result.applied ? 'success' : 'warning');
    } catch (error) {
      showToast(`Failed to schedule comfort: ${error}`, 'error');
    }
  };

  const handleGenerateSchedule = async () => {
    try {
      await schedulePreview.mutateAsync();
    } catch (error) {
      showToast(`Failed to generate schedule: ${error}`, 'error');
    }
  };

  const handleApplySchedule = () => {
    if (!schedulePreview.data?.schedulePayload) {
      showToast('No schedule to apply. Generate a schedule first.', 'error');
      return;
    }
    setApplyDialog(true);
  };

  const confirmApplySchedule = async () => {
    setApplyDialog(false);
    if (!deviceIds.gateway || !deviceIds.embedded) {
      showToast('Gateway Device ID and Embedded ID are required', 'error');
      return;
    }
    try {
      await applySchedule.mutateAsync({
        gatewayDeviceId: deviceIds.gateway,
        embeddedId: deviceIds.embedded,
        schedulePayload: schedulePreview.data!.schedulePayload!,
      });
      showToast('Schedule applied successfully!', 'success');
    } catch (error) {
      showToast(`Failed to apply schedule: ${error}`, 'error');
    }
  };

  const handleRetrieveCurrentSchedule = async () => {
    try {
      await currentSchedule.mutateAsync(deviceIds.embedded || undefined);
      showToast('Current schedule retrieved', 'success');
    } catch (error) {
      showToast(`Failed to retrieve schedule: ${error}`, 'error');
    }
  };

  return (
    <div className="space-y-6">
      {/* Auth Section */}
      <Card>
        <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
          <div className="flex items-center gap-3">
            <h2 className="text-xl font-semibold">Daikin Authorization</h2>
            <AuthStatusBadge />
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          {!isAuthorized ? (
            <button onClick={startAuth} className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors">
              Start OAuth Flow
            </button>
          ) : (
            <button onClick={() => refresh()} disabled={isRefreshing} className="px-4 py-2 border border-gray-300 dark:border-gray-700 text-sm font-medium rounded-lg hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors disabled:opacity-50">
              {isRefreshing ? 'Refreshing...' : 'Refresh Token'}
            </button>
          )}
        </div>
      </Card>

      {/* Price Chart */}
      <PriceChart />

      {/* Schedule Preview */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Schedule Preview</h2>
        {!isAuthorized && (
          <div className="bg-yellow-50 dark:bg-yellow-950 border border-yellow-200 dark:border-yellow-800 rounded-lg p-3 text-sm text-yellow-700 dark:text-yellow-400 mb-4">
            Authorize with Daikin to apply schedules to your device
          </div>
        )}
        <button onClick={handleGenerateSchedule} disabled={schedulePreview.isPending} className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 mb-4">
          {schedulePreview.isPending ? 'Generating...' : 'Generate Schedule'}
        </button>
        {schedulePreview.isError && (
          <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-3 text-sm text-red-600 dark:text-red-400 mb-4">
            Failed to generate schedule: {schedulePreview.error.message}
          </div>
        )}
        {schedulePreview.data && (
          <div>
            <ScheduleGrid schedulePayload={schedulePreview.data.schedulePayload} />
            <ScheduleLegend />
            {schedulePreview.data.message && (
              <p className="text-sm text-gray-500 dark:text-gray-400 mt-3">{schedulePreview.data.message}</p>
            )}
          </div>
        )}
      </Card>

      {/* Manual Comfort Run */}
      <Card>
        <h2 className="text-xl font-semibold mb-2">Manual Comfort Run</h2>
        <p className="text-sm text-gray-500 dark:text-gray-400 mb-4">
          Schedule an immediate comfort run (e.g., for filling a hot tub). Select a time within the next 48 hours.
        </p>
        <div className="flex flex-col sm:flex-row gap-3 items-end">
          <div className="flex-1 w-full">
            <label className="block text-sm font-medium mb-1">Comfort Time</label>
            <input
              type="datetime-local"
              value={manualComfortTime}
              onChange={(e) => setManualComfortTime(e.target.value)}
              min={formatDateTimeLocal(new Date())}
              max={formatDateTimeLocal(new Date(Date.now() + 48 * 60 * 60 * 1000))}
              className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors"
            />
          </div>
          <button onClick={handleManualComfort} disabled={!isAuthorized || !manualComfortTime || manualComfort.isPending} className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 whitespace-nowrap">
            {manualComfort.isPending ? 'Scheduling...' : 'Schedule & Apply'}
          </button>
        </div>
        {!isAuthorized && (
          <div className="bg-yellow-50 dark:bg-yellow-950 border border-yellow-200 dark:border-yellow-800 rounded-lg p-3 text-sm text-yellow-700 dark:text-yellow-400 mt-4">
            Authorize with Daikin before scheduling a manual comfort run.
          </div>
        )}
      </Card>

      {/* Flexible Scheduling Status - ONLY shown when Flexible mode */}
      {isFlexible && flexibleState && (
        <Card>
          <h2 className="text-xl font-semibold mb-4">Flexible Scheduling Status</h2>
          <div className="space-y-4">
            {/* Eco Status */}
            <div>
              <h3 className="font-medium mb-1">Eco (Daily DHW)</h3>
              <p className="text-sm text-gray-500 dark:text-gray-400">
                Last scheduled: {flexibleState.LastEcoRunUtc ? new Date(flexibleState.LastEcoRunUtc).toLocaleString() : 'Never (waiting for first interval)'}
              </p>
              {flexibleState.EcoWindow.Start && flexibleState.EcoWindow.End && (
                <p className="text-sm text-gray-500 dark:text-gray-400">
                  Next window: {new Date(flexibleState.EcoWindow.Start).toLocaleString()} – {new Date(flexibleState.EcoWindow.End).toLocaleString()}
                </p>
              )}
            </div>
            <hr className="border-gray-200 dark:border-gray-800" />
            {/* Comfort Status */}
            <div>
              <h3 className="font-medium mb-1">Comfort (Legionella)</h3>
              <p className="text-sm text-gray-500 dark:text-gray-400">
                Last run: {flexibleState.LastComfortRunUtc ? new Date(flexibleState.LastComfortRunUtc).toLocaleString() : 'Never (waiting for first interval)'}
              </p>
              {flexibleState.NextScheduledComfortUtc && (
                <p className="text-sm text-blue-600 dark:text-blue-400">
                  Next scheduled: {new Date(flexibleState.NextScheduledComfortUtc).toLocaleString()}
                </p>
              )}
              {flexibleState.ComfortWindow.Start && flexibleState.ComfortWindow.End && (
                <>
                  <p className="text-sm text-gray-500 dark:text-gray-400">
                    Window: {new Date(flexibleState.ComfortWindow.Start).toLocaleString()} – {new Date(flexibleState.ComfortWindow.End).toLocaleString()}
                  </p>
                  {flexibleState.ComfortWindow.Progress !== null && (
                    <div className="mt-2">
                      <span className="text-xs text-gray-500 dark:text-gray-400">
                        Window progress: {(flexibleState.ComfortWindow.Progress * 100).toFixed(0)}%
                      </span>
                      <div className="mt-1 h-2 rounded-full bg-gray-200 dark:bg-gray-800 overflow-hidden">
                        <div
                          className={`h-full rounded-full transition-all duration-300 ${(flexibleState.ComfortWindow.Progress ?? 0) > 0.9 ? 'bg-yellow-500' : 'bg-blue-500'}`}
                          style={{ width: `${(flexibleState.ComfortWindow.Progress ?? 0) * 100}%` }}
                        />
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
          </div>
        </Card>
      )}

      {/* Apply Schedule to Daikin */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Apply Schedule to Daikin</h2>
        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium mb-1">Gateway Device ID</label>
            <input type="text" value={deviceIds.gateway} onChange={(e) => setDeviceIds({ ...deviceIds, gateway: e.target.value })} placeholder="Enter gateway device ID" disabled={!isAuthorized} className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors disabled:opacity-50" />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Embedded ID</label>
            <input type="text" value={deviceIds.embedded} onChange={(e) => setDeviceIds({ ...deviceIds, embedded: e.target.value })} placeholder="Enter embedded ID" disabled={!isAuthorized} className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors disabled:opacity-50" />
          </div>
          <div className="flex flex-wrap gap-2">
            <button onClick={handleApplySchedule} disabled={!isAuthorized || !schedulePreview.data?.schedulePayload || applySchedule.isPending} className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50">
              {applySchedule.isPending ? 'Applying...' : 'Apply Schedule'}
            </button>
            <button onClick={handleRetrieveCurrentSchedule} disabled={!isAuthorized || currentSchedule.isPending} className="px-4 py-2 border border-gray-300 dark:border-gray-700 text-sm font-medium rounded-lg hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors disabled:opacity-50">
              {currentSchedule.isPending ? 'Retrieving...' : 'Retrieve Current Schedule'}
            </button>
          </div>
        </div>
        {!!currentSchedule.data && (
          <div className="mt-4">
            <h3 className="text-lg font-medium mb-2">Current Schedule</h3>
            <JsonViewer data={currentSchedule.data} />
          </div>
        )}
      </Card>

      {/* Schedule History */}
      <Card>
        <h2 className="text-xl font-semibold mb-4">Schedule History</h2>
        <ScheduleHistoryList />
      </Card>

      {/* Confirm Dialog */}
      <ConfirmDialog
        open={applyDialog}
        title="Apply Schedule"
        message="Are you sure you want to apply this schedule to your Daikin device? This will replace the current schedule."
        confirmText="Apply"
        cancelText="Cancel"
        onConfirm={confirmApplySchedule}
        onCancel={() => setApplyDialog(false)}
      />
    </div>
  );
}
