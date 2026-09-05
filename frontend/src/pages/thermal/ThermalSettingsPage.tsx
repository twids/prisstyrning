import { useEffect, useMemo, useState } from 'react';
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip,
  CircularProgress, FormControlLabel, IconButton, Paper, Stack, Switch, Tab, Tabs, TextField, Typography,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import AddIcon from '@mui/icons-material/Add';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import SaveOutlinedIcon from '@mui/icons-material/SaveOutlined';
import CableOutlinedIcon from '@mui/icons-material/CableOutlined';
import { useHomeAssistant, useSaveThermalConfig, useThermalConfig } from '../../hooks/thermal/useThermal';
import { PageHeader, formatRelative } from '../../components/thermal/thermalUi';
import HomeAssistantEntityPicker, { type EntityCatalogView } from '../../components/thermal/HomeAssistantEntityPicker';
import WeatherSourcePicker from '../../components/thermal/WeatherSourcePicker';
import HomeAssistantLiveStatus from '../../components/thermal/HomeAssistantLiveStatus';
import { assessEntityChoice } from '../../components/thermal/entityCatalog';
import { assessHomeAssistantLive } from '../../components/thermal/homeAssistantConnectionStatus';
import ConfirmDialog from '../../components/ConfirmDialog';
import type { HomeAssistantConnection, HomeAssistantEntity, ThermalConfig, ThermalEntityConfig, ThermalRoomConfig, UpdateHomeAssistantConnection } from '../../types/api';

const roles = [
  ['outside_temperature', 'Utetemperatur', '°C'],
  ['leaving_water_temperature', 'Framledning (LWT)', '°C'],
  ['return_water_temperature', 'Returledning (RWT)', '°C'],
  ['flow', 'Flöde', 'l/min'],
  ['brine_in', 'Köldbärare in', '°C'],
  ['brine_out', 'Köldbärare ut', '°C'],
  ['tank_temperature', 'Tanktemperatur', '°C'],
  ['heat_pump_power', 'Värmepumpens effekt', 'kW'],
  ['property_power', 'Fastighetens totaleffekt', 'kW'],
  ['dhw_active', 'DHW aktiv', 'bool'],
  ['defrost_active', 'Avfrostning aktiv', 'bool'],
  ['backup_heater_active', 'Elpatron aktiv', 'bool'],
  ['spot_price', 'Spotpris', 'SEK/kWh'],
  ['heating_deviation', 'P1P2 LWT-avvikelse', '°C'],
  ['weather_forecast', 'Väderprognos', 'forecast'],
  ['wind_speed', 'Vindhastighet', 'm/s'],
  ['solar_irradiance', 'Solinstrålning', 'W/m²'],
] as const;

export default function ThermalSettingsPage() {
  const config = useThermalConfig();
  const save = useSaveThermalConfig();
  const ha = useHomeAssistant();
  const [tab, setTab] = useState(0);
  const [draft, setDraft] = useState<ThermalConfig | null>(null);
  const [savedSnapshot, setSavedSnapshot] = useState('');
  const [nowUtc, setNowUtc] = useState(Date.now);
  useEffect(() => {
    const timer = window.setInterval(() => setNowUtc(Date.now()), 30_000);
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    if (config.data && !draft) {
      const copy = structuredClone(config.data);
      setDraft(copy);
      setSavedSnapshot(JSON.stringify(copy));
    }
  }, [config.data, draft]);
  const dirty = draft != null && JSON.stringify(draft) !== savedSnapshot;
  const configErrors = useMemo(() => validate(draft), [draft]);
  const live = assessHomeAssistantLive(ha.config.data, ha.status.data, ha.status.dataUpdatedAt, Math.max(nowUtc, Date.now()),
    ha.config.isLoading || ha.status.isLoading, ha.config.isError || ha.status.isError);
  const catalogIssue = ha.config.isError || ha.status.isError || ha.entities.isError
    ? 'Sensorlistan kunde inte uppdateras. Sparade mappningar är kvar; gamla värden visas inte som verifierade.'
    : !live.verified ? `${live.label}. ${live.detail} Sparade mappningar är kvar.` : undefined;
  const catalog: EntityCatalogView = {
    entities: catalogIssue ? [] : ha.entities.data ?? [],
    issue: catalogIssue,
    loading: ha.status.isLoading || ha.entities.isLoading,
    nowUtc,
  };
  const refreshCatalog = async () => {
    const [connection, status] = await Promise.all([ha.config.refetch(), ha.status.refetch()]);
    if (!connection.isError && !status.isError && status.data?.configured) await ha.entities.refetch();
  };

  const errors = [...configErrors, ...validateSensorMappings(draft, catalog)];

  const persist = async () => {
    if (!draft || errors.length) return;
    const result = await save.mutateAsync(draft);
    setDraft(structuredClone(result));
    setSavedSnapshot(JSON.stringify(result));
  };

  if (config.isError) return <Alert severity="error">{config.error.message}</Alert>;
  if (config.isLoading || !draft) return <CircularProgress aria-label="Laddar inställningar" />;
  return (
    <Stack spacing={4}>
      <PageHeader
        eyebrow="Konfiguration med skyddsräcken"
        title="Inställningar"
        description="Anslutningar och sensorer gäller ditt Daikin-konto. Hemligheter sparas krypterat och visas inte igen."
        action={<Stack direction="row" gap={1} alignItems="center">{dirty && <Chip color="warning" label="Osparade ändringar" />}<Button variant="contained" startIcon={<SaveOutlinedIcon />} onClick={persist} disabled={!dirty || errors.length > 0 || save.isPending}>{save.isPending ? 'Sparar…' : 'Spara'}</Button></Stack>}
      />
      {save.isSuccess && <Alert severity="success">Inställningarna är sparade. Driftläge och DHW-writer ändrades inte.</Alert>}
      {save.isError && <Alert severity="error">{save.error.message}</Alert>}
      {errors.map((error) => <Alert key={error} severity="error">{error}</Alert>)}
      <Paper variant="outlined">
        <Tabs value={tab} onChange={(_, value) => setTab(value)} variant="scrollable" scrollButtons="auto" aria-label="Inställningsområden" sx={{ borderBottom: 1, borderColor: 'divider', px: 1 }}>
          <Tab label="Home Assistant" /><Tab label="Entities" /><Tab label="Rum" /><Tab label="Kostnader" /><Tab label="Säkerhet" />
        </Tabs>
        <Box sx={{ p: { xs: 2, md: 3 } }}>
          {tab === 0 && <HomeAssistantTab ha={ha} />}
          {tab === 1 && <Stack spacing={2}><EntityCatalogNotice catalog={catalog} refresh={() => void refreshCatalog()} /><EntitiesTab draft={draft} setDraft={setDraft} catalog={catalog} /></Stack>}
          {tab === 2 && <Stack spacing={2}><EntityCatalogNotice catalog={catalog} refresh={() => void refreshCatalog()} /><RoomsTab draft={draft} setDraft={setDraft} catalog={catalog} /></Stack>}
          {tab === 3 && <CostsTab draft={draft} setDraft={setDraft} />}
          {tab === 4 && <SafetyTab draft={draft} setDraft={setDraft} />}
        </Box>
      </Paper>
      {dirty && (
        <Paper variant="outlined" sx={{ p: 3, borderColor: 'warning.main' }}>
          <Typography variant="h6">Konsekvens före sparande</Typography>
          <Typography color="text.secondary">Ändringen påverkar kommande shadowberäkningar. Den aktiverar aldrig ett driftläge, flyttar aldrig DHW-writern och skickar inget kommando. Effekttariff bidrar med {draft.site.tariffEnabled ? 'den konfigurerade kostnaden' : 'exakt 0 kr'}.</Typography>
        </Paper>
      )}
    </Stack>
  );
}

function HomeAssistantTab({ ha }: { ha: ReturnType<typeof useHomeAssistant> }) {
  if (ha.config.isLoading) return <CircularProgress aria-label="Laddar Home Assistant-anslutning" />;
  if (ha.config.isError) return <Alert severity="error">{ha.config.error.message}</Alert>;
  return <HomeAssistantConnectionPanel key={ha.config.data?.updatedAtUtc ?? 'new'} ha={ha} connection={ha.config.data ?? null} />;
}

export function HomeAssistantConnectionPanel({ ha, connection }: { ha: ReturnType<typeof useHomeAssistant>; connection: HomeAssistantConnection | null }) {
  const now = useMemo(() => new Date(), []);
  const initial = useMemo<UpdateHomeAssistantConnection>(() => ({
    baseUrl: connection?.baseUrl ?? '',
    telemetryToken: null,
    controlToken: null,
    telemetryEnabled: connection?.telemetryEnabled ?? true,
    controlEnabled: connection?.controlEnabled ?? false,
    heatingDeviationEntityId: connection?.heatingDeviationEntityId ?? '',
    staleAfterMinutes: connection?.staleAfterMinutes ?? 10,
    clearControlToken: false,
  }), [connection]);
  const [connectionDraft, setConnectionDraft] = useState(initial);
  const [removeOpen, setRemoveOpen] = useState(false);
  const [historyFrom, setHistoryFrom] = useState(() => toLocalDateTimeInput(new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000)));
  const [historyTo, setHistoryTo] = useState(() => toLocalDateTimeInput(now));
  const connectionErrors = useMemo(() => validateHomeAssistantConnection(connectionDraft, connection), [connectionDraft, connection]);
  const connectionDirty = JSON.stringify(connectionDraft) !== JSON.stringify(initial);
  const saveConnection = () => ha.save.mutate({
    ...connectionDraft,
    baseUrl: connectionDraft.baseUrl.trim(),
    telemetryToken: connectionDraft.telemetryToken?.trim() || null,
    controlToken: connectionDraft.controlToken?.trim() || null,
    heatingDeviationEntityId: connectionDraft.heatingDeviationEntityId.trim(),
  });
  const importHistory = () => ha.importHistory.mutate({
    fromUtc: new Date(historyFrom).toISOString(),
    toUtc: new Date(historyTo).toISOString(),
  });
  return <Stack spacing={3}>
    <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2}>
      <Box><Typography component="h2" variant="h5">Ditt kontos Home Assistant</Typography><Typography color="text.secondary">Anslutningen följer det verifierade Daikin-kontot. Telemetritoken bör vara läsande; styrtoken används bara för exakt tillåten P1P2-entity i aktiva lägen.</Typography></Box>
      <Stack direction="row" gap={1} alignItems="center" flexWrap="wrap"><Chip label={connection ? 'Anslutning sparad' : 'Ej konfigurerad'} variant="outlined" /><Button variant="outlined" startIcon={<CableOutlinedIcon />} onClick={() => ha.test.mutate()} disabled={!connection?.telemetryEnabled || connectionDirty || ha.test.isPending}>{ha.test.isPending ? 'Testar…' : 'Testa sparad anslutning'}</Button></Stack>
    </Stack>
    <Alert severity="info">Tokenvärden skickas över den inloggade HTTPS-sessionen, krypteras med installationens credential-nyckel och returneras aldrig av API:t. Tomma tokenfält behåller redan sparade tokens.</Alert>
    <Paper variant="outlined" sx={{ p: 2.5 }}>
      <Stack spacing={2.5}>
        <Stack direction={{ xs: 'column', md: 'row' }} gap={2} alignItems={{ md: 'flex-start' }}>
          <TextField fullWidth required label="Home Assistant-adress" placeholder="https://ha.example.se" value={connectionDraft.baseUrl} onChange={(event) => setConnectionDraft({ ...connectionDraft, baseUrl: event.target.value })} helperText="Publik HTTPS-adress utan sökvägsparametrar eller inloggningsuppgifter." />
          <TextField type="number" label="Gammal efter, minuter" value={connectionDraft.staleAfterMinutes} onChange={(event) => setConnectionDraft({ ...connectionDraft, staleAfterMinutes: Number(event.target.value) })} helperText="Standard för givarrapporter. Enskilda gränser anges under Entities/Rum. HA-kommunikation kontrolleras separat inom tio minuter." inputProps={{ min: 1, max: 60 }} sx={{ minWidth: 190 }} />
        </Stack>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography fontWeight={750}>Telemetri</Typography>
            <Typography variant="body2" color="text.secondary" mb={2}>Använd en separat, läsande långlivad HA-token.</Typography>
            <TextField fullWidth type="password" autoComplete="new-password" label={connection?.telemetryTokenConfigured ? 'Ny telemetritoken (valfritt)' : 'Telemetritoken'} value={connectionDraft.telemetryToken ?? ''} onChange={(event) => setConnectionDraft({ ...connectionDraft, telemetryToken: event.target.value || null })} helperText={connection?.telemetryTokenConfigured ? 'En token är sparad. Lämna tomt för att behålla den.' : 'Krävs första gången.'} />
            <FormControlLabel sx={{ mt: 1 }} control={<Switch checked={connectionDraft.telemetryEnabled} onChange={(event) => setConnectionDraft({ ...connectionDraft, telemetryEnabled: event.target.checked })} />} label="Samla telemetri" />
          </Paper>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography fontWeight={750}>P1P2-styrning</Typography>
            <Typography variant="body2" color="text.secondary" mb={2}>Endast <code>number.set_value</code> till entityn nedan är tillåtet.</Typography>
            <TextField fullWidth type="password" autoComplete="new-password" label={connection?.controlTokenConfigured ? 'Ny styrtoken (valfritt)' : 'Styrtoken'} value={connectionDraft.controlToken ?? ''} onChange={(event) => setConnectionDraft({ ...connectionDraft, controlToken: event.target.value || null, clearControlToken: false })} helperText={connection?.controlTokenConfigured ? 'En styrtoken är sparad. Lämna tomt för att behålla den.' : 'Behövs först när styrning ska aktiveras.'} />
            <TextField fullWidth sx={{ mt: 2 }} label="Tillåten LWT-avvikelse-entity" placeholder="number.daikin_deviation_heating" value={connectionDraft.heatingDeviationEntityId} onChange={(event) => setConnectionDraft({ ...connectionDraft, heatingDeviationEntityId: event.target.value })} />
            <FormControlLabel sx={{ mt: 1 }} control={<Switch checked={connectionDraft.controlEnabled} onChange={(event) => setConnectionDraft({ ...connectionDraft, controlEnabled: event.target.checked })} />} label="Tillåt styrklienten" />
            {connection?.controlTokenConfigured && <FormControlLabel control={<Switch checked={connectionDraft.clearControlToken} onChange={(event) => setConnectionDraft({ ...connectionDraft, clearControlToken: event.target.checked, controlEnabled: event.target.checked ? false : connectionDraft.controlEnabled, controlToken: null })} />} label="Ta bort sparad styrtoken vid nästa sparande" />}
          </Paper>
        </Box>
        {connectionErrors.map((error) => <Alert key={error} severity="error">{error}</Alert>)}
        {ha.save.isSuccess && <Alert severity="info">Anslutningen är sparad för ditt Daikin-konto. Liveanslutningen kontrolleras separat nedan. Inget driftläge aktiverades.</Alert>}
        {ha.save.isError && <Alert severity="error">{ha.save.error.message}</Alert>}
        {connectionDirty && <Alert severity="warning">Förhandsvisning: när du sparar avslutas den gamla telemetrianslutningen och dess cache töms. {connectionDraft.telemetryEnabled ? 'En ny anslutning och startbild hämtas automatiskt.' : 'Telemetriinsamlingen stoppas.'} Sparade sensormappningar och legacy-DHW ändras inte. LWT-kommandon är fortfarande blockerade i Legacy och Shadow.</Alert>}
        <Stack direction={{ xs: 'column-reverse', sm: 'row' }} justifyContent="space-between" gap={1}>
          <Button color="error" onClick={() => setRemoveOpen(true)} disabled={!connection || ha.remove.isPending}>Ta bort anslutning</Button>
          <Button variant="contained" startIcon={<SaveOutlinedIcon />} onClick={saveConnection} disabled={!connectionDirty || connectionErrors.length > 0 || ha.save.isPending}>{ha.save.isPending ? 'Sparar…' : 'Spara HA-anslutning'}</Button>
        </Stack>
      </Stack>
    </Paper>
    {ha.test.isSuccess && <Alert severity="info">REST-testet lyckades med den sparade telemetritoken. Det bekräftar inte WebSocket eller aktuell sensordata; se Liveanslutning.</Alert>}
    {ha.test.isError && <Alert severity="error">{ha.test.error.message}</Alert>}
    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
      <HomeAssistantLiveStatus connection={connection} status={ha.status.data} checkedAt={ha.status.dataUpdatedAt}
        loading={ha.status.isLoading} error={ha.status.isError} refreshing={ha.status.isFetching}
        refresh={() => { void ha.status.refetch(); }} />
      <Paper variant="outlined" sx={{ p: 2.5 }}><Typography fontWeight={750}>Sparat för kontot</Typography><Stack direction="row" justifyContent="space-between"><Typography>Telemetritoken</Typography><Typography fontWeight={700}>{connection?.telemetryTokenConfigured ? 'Sparad' : 'Saknas'}</Typography></Stack><Stack direction="row" justifyContent="space-between"><Typography>Styrtoken</Typography><Typography fontWeight={700}>{connection?.controlTokenConfigured ? 'Sparad' : 'Saknas'}</Typography></Stack><Stack direction="row" justifyContent="space-between"><Typography>Senast ändrad</Typography><Typography fontWeight={700}>{formatRelative(connection?.updatedAtUtc)}</Typography></Stack></Paper>
    </Box>
    <Paper variant="outlined" sx={{ p: 2.5 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2} alignItems={{ md: 'flex-end' }}>
        <Box sx={{ flex: 1 }}><Typography component="h3" variant="h6">Historik för modellträning</Typography><Typography variant="body2" color="text.secondary">Hämta förändringshistorik från HA och återsampla den till fem minuter. Intervallet får vara högst 90 dagar och befintliga snapshots skrivs aldrig över. Importerade punkter valideras separat; de är inte godkänd liveinsamling eller verifierade Shadow-dygn.</Typography></Box>
        <Stack direction={{ xs: 'column', sm: 'row' }} gap={1.5}>
          <TextField type="datetime-local" label="Från" value={historyFrom} onChange={(event) => setHistoryFrom(event.target.value)} InputLabelProps={{ shrink: true }} />
          <TextField type="datetime-local" label="Till" value={historyTo} onChange={(event) => setHistoryTo(event.target.value)} InputLabelProps={{ shrink: true }} />
          <Button variant="outlined" onClick={importHistory} disabled={!ha.status.data?.configured || ha.importHistory.isPending || !historyFrom || !historyTo}>{ha.importHistory.isPending ? 'Importerar…' : 'Importera'}</Button>
        </Stack>
      </Stack>
      {ha.importHistory.isSuccess && <Alert severity={ha.importHistory.data.entitiesWithoutHistory.length ? 'warning' : 'success'} sx={{ mt: 2 }}>{ha.importHistory.data.importedSamples} nya punkter importerades och {ha.importHistory.data.existingSamplesPreserved} befintliga bevarades.{ha.importHistory.data.entitiesWithoutHistory.length > 0 ? ` Användbar, tidsstämplad historik saknades för: ${ha.importHistory.data.entitiesWithoutHistory.join(', ')}.` : ''}</Alert>}
      {ha.importHistory.isError && <Alert severity="error" sx={{ mt: 2 }}>{ha.importHistory.error.message}</Alert>}
    </Paper>
    <ConfirmDialog open={removeOpen} title="Ta bort Home Assistant-anslutningen?" message="Telemetri och entity-listan stoppas för kontot. Åtgärden är blockerad i aktiva LWT-lägen och ändrar aldrig legacy-DHW." confirmText="Ta bort" cancelText="Avbryt" isDestructive onCancel={() => setRemoveOpen(false)} onConfirm={() => { setRemoveOpen(false); ha.remove.mutate(); }} />
  </Stack>;
}

export function validateHomeAssistantConnection(draft: UpdateHomeAssistantConnection, saved: HomeAssistantConnection | null): string[] {
  const errors: string[] = [];
  try {
    const url = new URL(draft.baseUrl);
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) errors.push('Home Assistant-adressen måste vara en HTTPS-adress utan användaruppgifter, query eller fragment.');
  } catch {
    errors.push('Ange en giltig fullständig Home Assistant-adress.');
  }
  if (draft.staleAfterMinutes < 1 || draft.staleAfterMinutes > 60) errors.push('Gammal-gränsen måste vara 1–60 minuter.');
  if (!saved?.telemetryTokenConfigured && !draft.telemetryToken?.trim()) errors.push('En separat telemetritoken krävs första gången.');
  const controlTokenAvailable = !draft.clearControlToken && (Boolean(draft.controlToken?.trim()) || saved?.controlTokenConfigured === true);
  if (draft.controlEnabled && !controlTokenAvailable) errors.push('Aktiv styrklient kräver en separat styrtoken.');
  if (draft.controlEnabled && !/^number\.[a-z0-9_.]+$/.test(draft.heatingDeviationEntityId.trim())) errors.push('Styrning kräver ett giltigt number-entity-ID för LWT-avvikelsen.');
  return [...new Set(errors)];
}

function EntityCatalogNotice({ catalog, refresh }: { catalog: EntityCatalogView; refresh: () => void }) {
  return <Stack spacing={1}>
    <Typography color="text.secondary">Ögonblickskontroll av värde, enhet och ålder – inte ett godkännande för aktiv styrning.</Typography>
    {catalog.issue && <Alert severity="warning">{catalog.issue}</Alert>}
    {catalog.loading && <Typography role="status">Hämtar sensorlistan…</Typography>}
    <Box><Button onClick={refresh} disabled={catalog.loading}>Uppdatera sensorlistan</Button></Box>
  </Stack>;
}

function EntitiesTab({ draft, setDraft, catalog }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void; catalog: EntityCatalogView }) {
  const updateRole = (role: string, selected: HomeAssistantEntity | null, unit: string) => {
    const rest = draft.entities.filter((entity) => entity.role !== role);
    const next: ThermalEntityConfig[] = selected ? [...rest, { id: 0, userId: draft.site.userId, role, entityId: selected.entityId, expectedUnit: unit, enabled: true, minimumValid: null, maximumValid: null, maximumRatePerHour: null }] : rest;
    setDraft({ ...draft, entities: next });
  };
  return <Stack spacing={2}>
    <Box><Typography variant="h5">Entity-mappning</Typography><Typography color="text.secondary">Välj datakälla för varje roll. Senast mottaget värde och preliminär kontroll visas även efter valet.</Typography></Box>
    <WeatherSourcePicker key={draft.entities.find((entity) => entity.role === 'weather_forecast')?.entityId ?? ''} catalog={catalog} entityId={draft.entities.find((entity) => entity.role === 'weather_forecast')?.entityId ?? ''} onChange={(selected) => updateRole('weather_forecast', selected, 'forecast')} />
    {roles.filter(([role]) => role !== 'weather_forecast').map(([role, label, unit]) => {
      const mapping = draft.entities.find((entity) => entity.role === role);
      return <Box key={role} sx={{ display: 'grid', gridTemplateColumns: { xs: 'minmax(0,1fr)', md: 'minmax(180px,.45fr) minmax(0,1fr)' }, gap: 2, alignItems: 'start', py: 1.5, borderBottom: 1, borderColor: 'divider' }}>
        <Box><Typography fontWeight={700}>{label}</Typography><Typography variant="caption" color="text.secondary">Förväntad enhet {unit}</Typography></Box>
        <Stack spacing={2}>
          <HomeAssistantEntityPicker catalog={catalog} entityId={mapping?.entityId ?? ''} expectedUnit={unit} rules={{ maximumReportAgeMinutes: mapping?.maximumReportAgeMinutes, minimum: mapping?.minimumValid, maximum: mapping?.maximumValid }} label={`Välj ${label.toLowerCase()}`} onChange={(selected) => updateRole(role, selected, unit)} />
          {mapping && <ReportAgeField label={`Rapportgräns för ${label.toLowerCase()}`} value={mapping.maximumReportAgeMinutes} maximum={['outside_temperature', 'wind_speed', 'solar_irradiance', 'spot_price'].includes(role) ? 1440 : 10} onChange={(value) => setDraft({ ...draft, entities: draft.entities.map((entity) => entity.role === role ? { ...entity, maximumReportAgeMinutes: value } : entity) })} />}
        </Stack>
      </Box>;
    })}
  </Stack>;
}

function RoomsTab({ draft, setDraft, catalog }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void; catalog: EntityCatalogView }) {
  const update = (index: number, changes: Partial<ThermalRoomConfig>) => setDraft({ ...draft, rooms: draft.rooms.map((room, roomIndex) => roomIndex === index ? { ...room, ...changes } : room) });
  const add = () => setDraft({ ...draft, rooms: [...draft.rooms, { id: 0, userId: draft.site.userId, name: `Rum ${draft.rooms.length + 1}`, entityId: '', targetOffsetC: 0, weight: 1, isCritical: false, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3 }] });
  return <Stack spacing={2}>
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" gap={1}><Box><Typography variant="h5">Rum och komfort</Typography><Typography color="text.secondary">Offset flyttar rummets mål; vikt styr husets representativa temperatur.</Typography></Box><Button startIcon={<AddIcon />} onClick={add}>Lägg till rum</Button></Stack>
    {draft.rooms.map((room, index) => <Paper key={`${room.id}-${index}`} variant="outlined" sx={{ p: 2, minWidth: 0 }}>
      <Stack spacing={2}>
        <TextField label="Namn" value={room.name} onChange={(event) => update(index, { name: event.target.value })} required />
        <HomeAssistantEntityPicker catalog={catalog} entityId={room.entityId} expectedUnit="°C" rules={{ maximumReportAgeMinutes: room.maximumReportAgeMinutes, minimum: room.minimumValidC, maximum: room.maximumValidC }} label={`Temperaturentity för ${room.name}`} required onChange={(entity) => update(index, { entityId: entity?.entityId ?? '' })} />
        <ReportAgeField label={`Rapportgräns för ${room.name}`} value={room.maximumReportAgeMinutes} maximum={1440} onChange={(value) => update(index, { maximumReportAgeMinutes: value })} />
        <Stack direction="row" gap={2} flexWrap="wrap" alignItems="center">
          <TextField type="number" label="Offset °C" value={room.targetOffsetC} onChange={(event) => update(index, { targetOffsetC: Number(event.target.value) })} inputProps={{ step: .1, min: -5, max: 5 }} sx={{ width: 110 }} />
          <TextField type="number" label="Vikt" value={room.weight} onChange={(event) => update(index, { weight: Number(event.target.value) })} inputProps={{ step: .1, min: 0, max: 100 }} sx={{ width: 100 }} />
          <FormControlLabel control={<Switch checked={room.isCritical} onChange={(event) => update(index, { isCritical: event.target.checked })} />} label={`Kritiskt rum: ${room.name}`} />
          <IconButton aria-label={`Ta bort ${room.name}`} onClick={() => setDraft({ ...draft, rooms: draft.rooms.filter((_, roomIndex) => roomIndex !== index) })}><DeleteOutlineIcon /></IconButton>
        </Stack>
      </Stack>
    </Paper>)}
  </Stack>;
}

function CostsTab({ draft, setDraft }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void }) {
  return <Stack spacing={3}><Box><Typography variant="h5">Elkostnad och tariff</Typography><Typography color="text.secondary">Spotpris kompletteras med konfigurerbara rörliga påslag. Nätbolag hårdkodas aldrig.</Typography></Box><FormControlLabel control={<Switch checked={draft.site.tariffEnabled} onChange={(event) => setDraft({ ...draft, site: { ...draft.site, tariffEnabled: event.target.checked } })} />} label="Ta med effekttariff i optimeringen" />{!draft.site.tariffEnabled && <Alert severity="info">Effekttariff är avstängd och bidrar med exakt 0 kr idag.</Alert>}<TextField label="Rörliga kostnadskomponenter (JSON)" value={draft.site.variableCostComponentsJson} onChange={(event) => setDraft({ ...draft, site: { ...draft.site, variableCostComponentsJson: event.target.value } })} multiline minRows={4} helperText={'Exempel: {"energiskatt": 0.55, "rörligt_nät": 0.12} i SEK/kWh'} /><Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Avancerat: tariffdefinition</Typography></AccordionSummary><AccordionDetails><TextField fullWidth multiline minRows={5} value={draft.site.tariffDefinitionJson} onChange={(event) => setDraft({ ...draft, site: { ...draft.site, tariffDefinitionJson: event.target.value } })} helperText="Modulärt format. Vattenfall eller något annat nätbolag hårdkodas inte." /></AccordionDetails></Accordion></Stack>;
}

function SafetyTab({ draft, setDraft }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void }) {
  const site = draft.site; const update = (changes: Partial<typeof site>) => setDraft({ ...draft, site: { ...site, ...changes } });
  return <Stack spacing={3}><Box><Typography variant="h5">Komfort och säkerhetsgränser</Typography><Typography color="text.secondary">Dessa värden påverkar readiness och hårda skydd, men byter aldrig driftläge i sig.</Typography></Box><Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', lg: 'repeat(4, 1fr)' }, gap: 2 }}><TextField type="number" label="Rumsmål °C" value={site.baseRoomTargetC} onChange={(event) => update({ baseRoomTargetC: Number(event.target.value) })} inputProps={{ step: .1, min: 10, max: 30 }} /><TextField type="number" label="Nedre band °C" value={site.lowerComfortBandC} onChange={(event) => update({ lowerComfortBandC: Number(event.target.value) })} inputProps={{ step: .1, min: 0, max: 5 }} /><TextField type="number" label="Övre band °C" value={site.upperComfortBandC} onChange={(event) => update({ upperComfortBandC: Number(event.target.value) })} inputProps={{ step: .1, min: 0, max: 5 }} /><TextField type="number" label="Max LWT-avvikelse °C" value={site.activeDeviationLimitC} onChange={(event) => update({ activeDeviationLimitC: Number(event.target.value) })} inputProps={{ step: .5, min: 0, max: 3 }} /></Box><Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}><Paper variant="outlined" sx={{ p: 2 }}><Typography fontWeight={750}>Verifieringar före LWT</Typography><FormControlLabel control={<Switch checked={site.weatherCurveVerified} onChange={(event) => update({ weatherCurveVerified: event.target.checked })} />} label="Grundkurvan är manuellt verifierad" /><FormControlLabel control={<Switch checked={site.heatPumpPowerSignVerified} onChange={(event) => update({ heatPumpPowerSignVerified: event.target.checked })} />} label="Shelly-tecken, CT och faser är verifierade" /></Paper><Paper variant="outlined" sx={{ p: 2 }}><Typography fontWeight={750}>Hygienpolicy</Typography><FormControlLabel control={<Switch checked={site.comfortSetpointConfirmed} onChange={(event) => update({ comfortSetpointConfirmed: event.target.checked })} />} label="Daikin comfort är manuellt satt till 60 °C" /><Stack direction="row" gap={2} mt={1}><TextField type="number" label="Intervall, dygn" value={site.comfortIntervalDays} onChange={(event) => update({ comfortIntervalDays: Number(event.target.value) })} /><TextField type="number" label="Flex ± dygn" value={site.comfortFlexibilityDays} onChange={(event) => update({ comfortFlexibilityDays: Number(event.target.value) })} /></Stack></Paper></Box><Alert severity="warning">Markera endast verifieringar du faktiskt har genomfört. UI:t ersätter inte kontroll på värmepumpen eller vid elcentralen.</Alert></Stack>;
}

function validate(config: ThermalConfig | null): string[] { if (!config) return []; const errors: string[] = []; if (config.site.baseRoomTargetC < 10 || config.site.baseRoomTargetC > 30) errors.push('Rumsmålet måste vara 10–30 °C.'); if (config.site.activeDeviationLimitC < 0 || config.site.activeDeviationLimitC > 3) errors.push('LWT-avvikelsen måste vara 0–3 °C.'); if (config.rooms.some((room) => !room.name.trim() || !room.entityId)) errors.push('Alla rum måste ha namn och temperaturentity.'); if (new Set(config.rooms.map((room) => room.entityId)).size !== config.rooms.length) errors.push('Samma temperaturentity kan inte användas av flera rum.'); try { JSON.parse(config.site.variableCostComponentsJson); } catch { errors.push('Rörliga kostnadskomponenter måste vara giltig JSON.'); } try { JSON.parse(config.site.tariffDefinitionJson); } catch { errors.push('Tariffdefinitionen måste vara giltig JSON.'); } return errors; }

function validateSensorMappings(config: ThermalConfig | null, catalog: EntityCatalogView): string[] {
  if (!config) return [];
  const errors: string[] = [];
  const check = (label: string, entityId: string, unit: string, age: number | null | undefined, maximum: number, minimumValue?: number | null, maximumValue?: number | null) => {
    if (age != null && (!Number.isInteger(age) || age < 1 || age > maximum)) errors.push(`${label}: rapportgränsen måste vara 1–${maximum} minuter.`);
    if (unit === 'forecast' && entityId.startsWith('weather.')) return; // Forecast capability is tested through the dedicated read-only HA action.
    const entity = catalog.entities.find((entry) => entry.entityId === entityId);
    if (!entity || catalog.issue) return;
    const result = assessEntityChoice(entity, unit, catalog.nowUtc, undefined, { maximumReportAgeMinutes: age, minimum: minimumValue, maximum: maximumValue });
    if (result.quality === 'Invalid') errors.push(`${label}: ${result.reason}`);
  };
  config.rooms.filter((room) => room.enabled).forEach((room) => check(room.name, room.entityId, '°C', room.maximumReportAgeMinutes, 1440, room.minimumValidC, room.maximumValidC));
  config.entities.filter((entity) => entity.enabled).forEach((entity) => check(roles.find(([role]) => role === entity.role)?.[1] ?? entity.role, entity.entityId, entity.expectedUnit, entity.maximumReportAgeMinutes, ['outside_temperature', 'wind_speed', 'solar_irradiance', 'spot_price'].includes(entity.role) ? 1440 : 10, entity.minimumValid, entity.maximumValid));
  return errors;
}

function ReportAgeField({ label, value, maximum, onChange }: { label: string; value?: number | null; maximum: number; onChange: (value: number | null) => void }) {
  return <TextField type="number" label={label} value={value ?? ''} onChange={(event) => onChange(event.target.value === '' ? null : Number(event.target.value))} inputProps={{ min: 1, max: maximum, step: 1 }} error={value != null && (!Number.isInteger(value) || value < 1 || value > maximum)} helperText={`Minuter utan ny rapport innan åldersvarning (1–${maximum}). Tomt använder kontots standard. Välj utifrån givarens verkliga rapportintervall; en längre gräns gör inte en gammal mätning ny.`} />;
}

function toLocalDateTimeInput(value: Date): string {
  const local = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}
