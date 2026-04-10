import { useState } from 'react';
import { Lightning } from '@phosphor-icons/react';
import { toast } from 'sonner';

import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

import ConnectionBadge from '../components/ConnectionBadge';
import PriceChart from '../components/PriceChart';
import TrendChart from '../components/TrendChart';
import HeatingTimeline from '../components/HeatingTimeline';
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
import { useFormatters } from '../context/TimezoneContext';

const formatDateTimeLocal = (date: Date): string => {
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

export default function DashboardPage() {
  const { isAuthorized, startAuth, refresh, isRefreshing } = useAuth();
  const schedulePreview = useSchedulePreview();
  const applySchedule = useApplySchedule();
  const currentSchedule = useCurrentSchedule();
  const { settings } = useUserSettings();
  const { formatDateTime } = useFormatters();
  const isFlexible = settings?.SchedulingMode === 'Flexible';
  const { state: flexibleState } = useFlexibleState(isFlexible);
  const manualComfort = useManualComfort();

  const [applyDialog, setApplyDialog] = useState(false);
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
      if (result.applied) {
        toast.success(result.message);
      } else {
        toast.warning(result.message);
      }
    } catch (error) {
      toast.error(`Misslyckades med att schemalägga komfort: ${error}`);
    }
  };

  const handleGenerateSchedule = async () => {
    try {
      await schedulePreview.mutateAsync();
    } catch (error) {
      toast.error(`Misslyckades med att generera schema: ${error}`);
    }
  };

  const handleApplySchedule = () => {
    if (!schedulePreview.data?.schedulePayload) {
      toast.error('Inget schema att applicera. Generera ett schema först.');
      return;
    }
    setApplyDialog(true);
  };

  const confirmApplySchedule = async () => {
    setApplyDialog(false);
    try {
      await applySchedule.mutateAsync({
        schedulePayload: schedulePreview.data!.schedulePayload!,
      });
      toast.success('Schema applicerat!');
    } catch (error) {
      toast.error(`Misslyckades med att applicera schema: ${error}`);
    }
  };

  const handleRetrieveCurrentSchedule = async () => {
    try {
      await currentSchedule.mutateAsync(undefined);
      toast.success('Aktuellt schema hämtat');
    } catch (error) {
      toast.error(`Misslyckades med att hämta schema: ${error}`);
    }
  };

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Lightning weight="fill" className="w-7 h-7 text-primary" />
          <h1 className="text-2xl font-semibold font-[Space_Grotesk]">Energi Dashboard</h1>
        </div>
        <ConnectionBadge />
      </div>

      {/* Auth Section */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Daikin Auktorisering</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-2">
            {!isAuthorized ? (
              <Button onClick={startAuth}>
                Starta OAuth-flöde
              </Button>
            ) : (
              <Button
                variant="outline"
                onClick={() => refresh()}
                disabled={isRefreshing}
              >
                {isRefreshing ? 'Uppdaterar...' : 'Uppdatera token'}
              </Button>
            )}
          </div>
        </CardContent>
      </Card>

      {/* Price Chart */}
      <PriceChart />

      {/* Trend Chart — kept as-is for Phase 4 */}
      <TrendChart />

      {/* Schedule Preview / Värmeschema */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Värmeschema</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          {!isAuthorized && (
            <p className="text-sm text-yellow-600 dark:text-yellow-400 border border-yellow-500/30 bg-yellow-500/10 rounded-md px-3 py-2">
              Auktorisera med Daikin för att applicera scheman på din enhet
            </p>
          )}

          <div className="flex flex-wrap gap-2">
            <Button
              onClick={handleGenerateSchedule}
              disabled={schedulePreview.isPending}
            >
              {schedulePreview.isPending ? 'Genererar...' : 'Generera schema'}
            </Button>

            <Button
              variant="outline"
              onClick={handleApplySchedule}
              disabled={!isAuthorized || !schedulePreview.data?.schedulePayload || applySchedule.isPending}
            >
              {applySchedule.isPending ? 'Applicerar...' : 'Applicera schema'}
            </Button>
          </div>

          {schedulePreview.isError && (
            <p className="text-sm text-destructive">
              Misslyckades med att generera schema: {schedulePreview.error.message}
            </p>
          )}

          {schedulePreview.data ? (
            <div>
              <HeatingTimeline
                schedulePayload={schedulePreview.data.schedulePayload}
              />
              {schedulePreview.data.message && (
                <p className="text-sm text-muted-foreground mt-2">
                  {schedulePreview.data.message}
                </p>
              )}
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">
              Inget schema genererat ännu. Klicka "Generera schema" för att börja.
            </p>
          )}
        </CardContent>
      </Card>

      {/* Manual Comfort Run */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Komfortboost</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-sm text-muted-foreground">
            Schemalägg en omedelbar komfortkörning (t.ex. för att fylla ett varmt badkar).
            Välj en tid inom de närmaste 48 timmarna.
          </p>
          <div className="flex flex-col sm:flex-row gap-2 items-end">
            <div className="flex-1">
              <label className="text-xs text-muted-foreground mb-1 block">Komforttid</label>
              <input
                type="datetime-local"
                value={manualComfortTime}
                onChange={(e) => setManualComfortTime(e.target.value)}
                min={formatDateTimeLocal(new Date())}
                max={formatDateTimeLocal(new Date(Date.now() + 48 * 60 * 60 * 1000))}
                className="w-full rounded-md border border-input bg-background px-3 py-2 text-sm ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2"
              />
            </div>
            <Button
              onClick={handleManualComfort}
              disabled={!isAuthorized || !manualComfortTime || manualComfort.isPending}
              className="whitespace-nowrap"
            >
              {manualComfort.isPending ? 'Schemalägger...' : 'Schemalägg & Applicera'}
            </Button>
          </div>
          {!isAuthorized && (
            <p className="text-sm text-yellow-600 dark:text-yellow-400">
              Auktorisera med Daikin innan du schemalägger en manuell komfortkörning.
            </p>
          )}
        </CardContent>
      </Card>

      {/* Flexible Scheduling Status */}
      {isFlexible && flexibleState && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Flexibel schemaläggning</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {/* Eco Status */}
            <div>
              <p className="text-sm font-semibold mb-1">Eco (Daglig VVB)</p>
              <p className="text-sm text-muted-foreground">
                Senast schemalagd:{' '}
                {flexibleState.LastEcoRunUtc
                  ? formatDateTime(flexibleState.LastEcoRunUtc)
                  : 'Aldrig (väntar på första intervall)'}
              </p>
              {flexibleState.EcoWindow.Start && flexibleState.EcoWindow.End && (
                <p className="text-sm text-muted-foreground">
                  Nästa fönster: {formatDateTime(flexibleState.EcoWindow.Start)} –{' '}
                  {formatDateTime(flexibleState.EcoWindow.End)}
                </p>
              )}
            </div>

            <div className="border-t border-border" />

            {/* Comfort Status */}
            <div>
              <p className="text-sm font-semibold mb-1">Komfort (Legionella)</p>
              <p className="text-sm text-muted-foreground">
                Senaste körning:{' '}
                {flexibleState.LastComfortRunUtc
                  ? formatDateTime(flexibleState.LastComfortRunUtc)
                  : 'Aldrig (väntar på första intervall)'}
              </p>
              {flexibleState.NextScheduledComfortUtc && (
                <p className="text-sm text-primary">
                  Nästa schemalagd: {formatDateTime(flexibleState.NextScheduledComfortUtc)}
                </p>
              )}
              {flexibleState.ComfortWindow.Start && flexibleState.ComfortWindow.End && (
                <div>
                  <p className="text-sm text-muted-foreground">
                    Fönster: {formatDateTime(flexibleState.ComfortWindow.Start)} –{' '}
                    {formatDateTime(flexibleState.ComfortWindow.End)}
                  </p>
                  {flexibleState.ComfortWindow.Progress !== null && (
                    <div className="mt-2">
                      <p className="text-xs text-muted-foreground mb-1">
                        Fönsterförlopp: {((flexibleState.ComfortWindow.Progress ?? 0) * 100).toFixed(0)}%
                      </p>
                      <div className="h-2 rounded-full bg-muted overflow-hidden">
                        <div
                          className="h-full rounded-full transition-all duration-300"
                          style={{
                            width: `${(flexibleState.ComfortWindow.Progress ?? 0) * 100}%`,
                            backgroundColor:
                              (flexibleState.ComfortWindow.Progress ?? 0) > 0.9
                                ? 'hsl(var(--warning))'
                                : 'hsl(var(--primary))',
                          }}
                        />
                      </div>
                    </div>
                  )}
                </div>
              )}
            </div>
          </CardContent>
        </Card>
      )}

      {/* Retrieve Current Schedule */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Hämta aktuellt schema</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-sm text-muted-foreground">
            Enhets-ID:n detekteras automatiskt från ditt Daikin-konto.
          </p>
          <Button
            variant="outline"
            onClick={handleRetrieveCurrentSchedule}
            disabled={!isAuthorized || currentSchedule.isPending}
          >
            {currentSchedule.isPending ? 'Hämtar...' : 'Hämta aktuellt schema'}
          </Button>

          {!!currentSchedule.data && (
            <div className="mt-3">
              <p className="text-sm font-medium mb-2">Aktuellt schema</p>
              <JsonViewer data={currentSchedule.data} />
            </div>
          )}
        </CardContent>
      </Card>

      {/* History */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Historik</CardTitle>
        </CardHeader>
        <CardContent>
          <ScheduleHistoryList />
        </CardContent>
      </Card>

      {/* Confirm Dialog */}
      <ConfirmDialog
        open={applyDialog}
        title="Applicera schema"
        message="Är du säker på att du vill applicera det här schemat på din Daikin-enhet? Det befintliga schemat kommer att ersättas."
        confirmText="Applicera"
        cancelText="Avbryt"
        onConfirm={confirmApplySchedule}
        onCancel={() => setApplyDialog(false)}
      />
    </div>
  );
}
