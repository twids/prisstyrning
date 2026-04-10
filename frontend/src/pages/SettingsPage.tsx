import { useState, useEffect } from 'react';
import { toast } from 'sonner';
import { FloppyDisk } from '@phosphor-icons/react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Slider } from '@/components/ui/slider';
import { Switch } from '@/components/ui/switch';
import { Label } from '@/components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Separator } from '@/components/ui/separator';
import { Badge } from '@/components/ui/badge';
import { useUserSettings } from '../hooks/useUserSettings';
import { useZone } from '../hooks/useZone';
import { useAuth } from '../hooks/useAuth';
import { useLocale } from '../context/TimezoneContext';
import ConfirmDialog from '../components/ConfirmDialog';

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
  const { isAuthorized, refresh, revoke, startAuth, isRefreshing } = useAuth();
  const {
    localeSetting,
    setLocaleSetting,
    systemLocale,
    systemTimezone,
    effectiveLocale,
    effectiveTimezone,
    localeOptions,
  } = useLocale();

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
        Timezone: settings?.Timezone ?? 'auto',
      });

      if (formData.selectedZone !== zone) {
        await setZone(formData.selectedZone);
      }

      toast.success('Inställningar sparade');
    } catch (err) {
      toast.error(`Kunde inte spara inställningar: ${err}`);
    }
  };

  const handleRevokeToken = async () => {
    setRevokeDialog(false);
    try {
      await revoke();
      toast.info('Daikin-autentisering återkallad');
    } catch (err) {
      toast.error(`Kunde inte återkalla: ${err}`);
    }
  };

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-primary border-t-transparent" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="mx-auto max-w-2xl px-4 py-8">
        <p className="text-destructive">Kunde inte ladda inställningar: {error.message}</p>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-2xl space-y-4 px-4 py-6">
      <h1 className="text-2xl font-semibold tracking-tight">Inställningar</h1>

      {/* Schedule Configuration */}
      <Card className="p-5 space-y-5">
        <h2 className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Schemakonfiguration</h2>

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Komforttimmar</Label>
            <span className="text-sm font-medium tabular-nums">{formData.comfortHours}</span>
          </div>
          <Slider
            value={[formData.comfortHours]}
            onValueChange={([value]) => setFormData(s => ({ ...s, comfortHours: value }))}
            min={1}
            max={12}
            step={1}
          />
          <p className="text-xs text-muted-foreground">Antal timmar per dag för att värma vatten till komforttemperatur</p>
        </div>

        <Separator />

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Avstängningsprocent</Label>
            <span className="text-sm font-medium tabular-nums">{(formData.turnOffPercentile * 100).toFixed(0)}%</span>
          </div>
          <Slider
            value={[formData.turnOffPercentile]}
            onValueChange={([value]) => setFormData(s => ({ ...s, turnOffPercentile: value }))}
            min={0.5}
            max={0.99}
            step={0.01}
          />
          <p className="text-xs text-muted-foreground">Priströskel för att stänga av varmvattenberedning (högre = färre avstängningar)</p>
        </div>

        <Separator />

        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <Label>Max komfortgap</Label>
            <span className="text-sm font-medium tabular-nums">{formData.maxComfortGapHours} h</span>
          </div>
          <Slider
            value={[formData.maxComfortGapHours]}
            onValueChange={([value]) => setFormData(s => ({ ...s, maxComfortGapHours: value }))}
            min={1}
            max={72}
            step={1}
          />
          <p className="text-xs text-muted-foreground">Maximalt gap mellan på varandra följande komforttimmar (1–72 h)</p>
        </div>

        <Separator />

        <div className="space-y-2">
          <Label>Priszon</Label>
          <Select
            value={formData.selectedZone}
            onValueChange={(value) => setFormData(s => ({ ...s, selectedZone: value }))}
          >
            <SelectTrigger>
              <SelectValue placeholder="Välj zon" />
            </SelectTrigger>
            <SelectContent>
              {ZONES.map((z) => (
                <SelectItem key={z} value={z}>{z}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </Card>

      {/* Scheduling Mode + Automation */}
      <Card className="p-5 space-y-5">
        <h2 className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Schemaläge</h2>

        <div className="space-y-2">
          <Label>Läge</Label>
          <Select
            value={formData.schedulingMode}
            onValueChange={(value) => setFormData(s => ({ ...s, schedulingMode: value as 'Classic' | 'Flexible' }))}
          >
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="Classic">Klassisk (Fast dagligt schema)</SelectItem>
              <SelectItem value="Flexible">Flexibel (Intervallbaserad med prisoptimering)</SelectItem>
            </SelectContent>
          </Select>
          <p className="text-xs text-muted-foreground">Klassiskt läge genererar ett fast dagligt schema. Flexibelt läge schemalägger eco- och komfortkörningar vid optimala priser inom konfigurerbara intervall.</p>
        </div>

        <Separator />

        <div className="flex items-center justify-between">
          <div className="space-y-1">
            <Label htmlFor="auto-apply">Auto-applicera</Label>
            <p className="text-xs text-muted-foreground">Applicera automatiskt genererat schema till din Daikin-enhet varje dag</p>
          </div>
          <Switch
            id="auto-apply"
            checked={formData.autoApplySchedule}
            onCheckedChange={(checked) => setFormData(s => ({ ...s, autoApplySchedule: checked }))}
          />
        </div>
      </Card>

      {/* Regional Formatting */}
      <Card className="p-5 space-y-4">
        <h2 className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Regionformat</h2>

        <div className="space-y-2">
          <Label>Locale</Label>
          <Select
            value={localeSetting}
            onValueChange={(value) => {
              const nextLocale = localeOptions.find((option) => option.value === value)?.value;
              if (nextLocale) setLocaleSetting(nextLocale);
            }}
          >
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {localeOptions.map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.value === 'auto'
                    ? `Auto (System) — ${systemLocale} · ${systemTimezone}`
                    : `${option.label} — ${option.locale} · ${option.timezone}`}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <p className="text-xs text-muted-foreground">
            Tillämpas direkt och sparas lokalt i webbläsaren. Aktivt format: {effectiveLocale} · {effectiveTimezone}.
          </p>
        </div>
      </Card>

      {/* Flexible Schedule Settings */}
      {formData.schedulingMode === 'Flexible' && (
        <Card className="p-5 space-y-5">
          <h2 className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Flexibla schemainställningar</h2>

          <h3 className="font-medium">Eco (Daglig VVB ~45°C)</h3>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label>Eco-intervall</Label>
              <span className="text-sm font-medium tabular-nums">{formData.ecoIntervalHours} h</span>
            </div>
            <Slider
              value={[formData.ecoIntervalHours]}
              onValueChange={([value]) => setFormData(s => ({ ...s, ecoIntervalHours: value }))}
              min={6}
              max={36}
              step={1}
            />
            <p className="text-xs text-muted-foreground">Hur ofta eco-uppvärmning ska köras (målintervall)</p>
          </div>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label>Eco-flexibilitet</Label>
              <span className="text-sm font-medium tabular-nums">±{formData.ecoFlexibilityHours} h</span>
            </div>
            <Slider
              value={[formData.ecoFlexibilityHours]}
              onValueChange={([value]) => setFormData(s => ({ ...s, ecoFlexibilityHours: value }))}
              min={1}
              max={18}
              step={1}
            />
            <p className="text-xs text-muted-foreground">
              Schemaläggningsfönster: eco körs mellan {Math.max(0, formData.ecoIntervalHours - formData.ecoFlexibilityHours)} h och {formData.ecoIntervalHours + formData.ecoFlexibilityHours} h efter senaste körning
            </p>
          </div>

          <Separator />

          <h3 className="font-medium">Comfort (Legionella ~60°C)</h3>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label>Comfort-intervall</Label>
              <span className="text-sm font-medium tabular-nums">{formData.comfortIntervalDays} dagar</span>
            </div>
            <Slider
              value={[formData.comfortIntervalDays]}
              onValueChange={([value]) => setFormData(s => ({ ...s, comfortIntervalDays: value }))}
              min={7}
              max={90}
              step={1}
            />
            <p className="text-xs text-muted-foreground">Hur ofta comfort-uppvärmning (legionella) ska köras</p>
          </div>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label>Comfort-flexibilitet</Label>
              <span className="text-sm font-medium tabular-nums">±{formData.comfortFlexibilityDays} dagar</span>
            </div>
            <Slider
              value={[formData.comfortFlexibilityDays]}
              onValueChange={([value]) => setFormData(s => ({ ...s, comfortFlexibilityDays: value }))}
              min={1}
              max={30}
              step={1}
            />
            <p className="text-xs text-muted-foreground">
              Schemaläggningsfönster: comfort körs mellan {Math.max(0, formData.comfortIntervalDays - formData.comfortFlexibilityDays)} d och {formData.comfortIntervalDays + formData.comfortFlexibilityDays} d efter senaste körning
            </p>
          </div>

          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <Label>Tidig comfort-tröskel</Label>
              <span className="text-sm font-medium tabular-nums">{(formData.comfortEarlyPercentile * 100).toFixed(0)}%</span>
            </div>
            <Slider
              value={[formData.comfortEarlyPercentile]}
              onValueChange={([value]) => setFormData(s => ({ ...s, comfortEarlyPercentile: value }))}
              min={0.01}
              max={0.50}
              step={0.01}
            />
            <p className="text-xs text-muted-foreground">
              När comfort-fönstret öppnar, utlös endast om priset är under denna historiska percentil. Tröskeln lättar allt eftersom fönstret fortskrider.
            </p>
          </div>
        </Card>
      )}

      {/* Daikin Connection */}
      <Card className="p-5 space-y-4">
        <h2 className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Daikin-anslutning</h2>

        <div className="flex items-center gap-3">
          <Badge variant={isAuthorized ? 'default' : 'secondary'}>
            {isAuthorized ? 'Ansluten' : 'Ej ansluten'}
          </Badge>
        </div>

        <div className="flex flex-wrap gap-2">
          {!isAuthorized ? (
            <Button onClick={() => startAuth()}>
              Starta OAuth-flöde
            </Button>
          ) : (
            <>
              <Button variant="outline" onClick={() => refresh()} disabled={isRefreshing}>
                {isRefreshing ? 'Uppdaterar…' : 'Uppdatera token'}
              </Button>
              <Button variant="destructive" onClick={() => setRevokeDialog(true)}>
                Återkalla
              </Button>
            </>
          )}
        </div>
      </Card>

      {/* Save Button */}
      <Button
        className="w-full"
        size="lg"
        onClick={handleSaveSettings}
        disabled={isUpdating || isZoneUpdating}
      >
        {isUpdating || isZoneUpdating ? (
          <>
            <span className="mr-2 h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent" />
            Sparar…
          </>
        ) : (
          <>
            <FloppyDisk className="mr-2 h-4 w-4" />
            Spara inställningar
          </>
        )}
      </Button>

      <ConfirmDialog
        open={revokeDialog}
        title="Återkalla Daikin-åtkomst"
        message="Är du säker på att du vill återkalla åtkomsten till ditt Daikin-konto? Du behöver auktorisera igen för att kunna använda schemaläggningsfunktioner."
        confirmText="Återkalla"
        cancelText="Avbryt"
        onConfirm={handleRevokeToken}
        onCancel={() => setRevokeDialog(false)}
        isDestructive
      />
    </div>
  );
}
