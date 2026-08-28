import { useEffect, useMemo, useState } from 'react';
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Autocomplete, Box, Button, Chip,
  CircularProgress, FormControlLabel, IconButton, Paper, Stack, Switch, Tab, Tabs, TextField, Typography,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import AddIcon from '@mui/icons-material/Add';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline';
import SaveOutlinedIcon from '@mui/icons-material/SaveOutlined';
import CableOutlinedIcon from '@mui/icons-material/CableOutlined';
import { useHomeAssistant, useSaveThermalConfig, useThermalConfig } from '../../hooks/thermal/useThermal';
import { PageHeader, QualityChip, formatRelative } from '../../components/thermal/thermalUi';
import type { HomeAssistantEntity, ThermalConfig, ThermalEntityConfig, ThermalRoomConfig } from '../../types/api';

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
  useEffect(() => {
    if (config.data && !draft) {
      const copy = structuredClone(config.data);
      setDraft(copy);
      setSavedSnapshot(JSON.stringify(copy));
    }
  }, [config.data, draft]);
  const dirty = draft != null && JSON.stringify(draft) !== savedSnapshot;
  const errors = useMemo(() => validate(draft), [draft]);

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
        description="Friendly names för vardagen, exakta entity-ID:n för spårbarhet. Hemligheter lagras aldrig i databasen eller visas i loggar."
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
          {tab === 1 && <EntitiesTab draft={draft} setDraft={setDraft} entities={ha.entities.data ?? []} />}
          {tab === 2 && <RoomsTab draft={draft} setDraft={setDraft} entities={ha.entities.data ?? []} />}
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
  const now = useMemo(() => new Date(), []);
  const [historyFrom, setHistoryFrom] = useState(() => toLocalDateTimeInput(new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000)));
  const [historyTo, setHistoryTo] = useState(() => toLocalDateTimeInput(now));
  const importHistory = () => ha.importHistory.mutate({
    fromUtc: new Date(historyFrom).toISOString(),
    toUtc: new Date(historyTo).toISOString(),
  });
  return <Stack spacing={3}>
    <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2}>
      <Box><Typography variant="h5">Separata identiteter</Typography><Typography color="text.secondary">Telemetritoken är läsande. Styrtoken har en kodmässig allowlist och används bara i aktiva lägen.</Typography></Box>
      <Stack direction="row" gap={1} alignItems="center"><Chip label={ha.status.data?.configured ? 'Konfigurerad' : 'Ej konfigurerad'} color={ha.status.data?.configured ? 'success' : 'default'} /><Button variant="outlined" startIcon={<CableOutlinedIcon />} onClick={() => ha.test.mutate()} disabled={ha.test.isPending}>{ha.test.isPending ? 'Testar…' : 'Testa anslutning'}</Button></Stack>
    </Stack>
    {ha.test.isSuccess && <Alert severity="success">Telemetriidentiteten når Home Assistant.</Alert>}
    {ha.test.isError && <Alert severity="error">{ha.test.error.message}</Alert>}
    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
      <Paper variant="outlined" sx={{ p: 2.5 }}><Typography fontWeight={750}>Telemetri</Typography><Typography variant="body2" color="text.secondary" mb={2}>Sökväg till Docker-secret: <code>HomeAssistant__Telemetry__TokenFile</code></Typography><Stack direction="row" justifyContent="space-between"><Typography>WebSocket/startbild</Typography><Typography fontWeight={700}>{ha.status.data?.connected ? 'Ansluten' : 'Frånkopplad'}</Typography></Stack><Stack direction="row" justifyContent="space-between"><Typography>Cache</Typography><Typography fontWeight={700}>{ha.status.data?.cachedEntities ?? 0} entities</Typography></Stack><Stack direction="row" justifyContent="space-between"><Typography>Senaste startbild</Typography><Typography fontWeight={700}>{formatRelative(ha.status.data?.lastSnapshotUtc)}</Typography></Stack><Stack direction="row" justifyContent="space-between"><Typography>Senaste aktivitet</Typography><Typography fontWeight={700}>{formatRelative(ha.status.data?.lastActivityUtc)}</Typography></Stack></Paper>
      <Paper variant="outlined" sx={{ p: 2.5 }}><Typography fontWeight={750}>Styrning</Typography><Typography variant="body2" color="text.secondary" mb={2}>Sökväg till Docker-secret: <code>HomeAssistant__Control__TokenFile</code></Typography><Typography variant="body2">Endast <code>number.set_value</code> till exakt konfigurerad <code>Deviation_Heating</code> kan skickas. Inget generellt serviceanrop exponeras.</Typography></Paper>
    </Box>
    <Paper variant="outlined" sx={{ p: 2.5 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2} alignItems={{ md: 'flex-end' }}>
        <Box sx={{ flex: 1 }}><Typography variant="h6">Historik för modellträning</Typography><Typography variant="body2" color="text.secondary">Hämta förändringshistorik från HA och återsampla den till fem minuter. Intervallet får vara högst 90 dagar och befintliga snapshots skrivs aldrig över.</Typography></Box>
        <Stack direction={{ xs: 'column', sm: 'row' }} gap={1.5}>
          <TextField type="datetime-local" label="Från" value={historyFrom} onChange={(event) => setHistoryFrom(event.target.value)} InputLabelProps={{ shrink: true }} />
          <TextField type="datetime-local" label="Till" value={historyTo} onChange={(event) => setHistoryTo(event.target.value)} InputLabelProps={{ shrink: true }} />
          <Button variant="outlined" onClick={importHistory} disabled={!ha.status.data?.configured || ha.importHistory.isPending || !historyFrom || !historyTo}>{ha.importHistory.isPending ? 'Importerar…' : 'Importera'}</Button>
        </Stack>
      </Stack>
      {ha.importHistory.isSuccess && <Alert severity={ha.importHistory.data.entitiesWithoutHistory.length ? 'warning' : 'success'} sx={{ mt: 2 }}>{ha.importHistory.data.importedSamples} nya punkter importerades och {ha.importHistory.data.existingSamplesPreserved} befintliga bevarades.{ha.importHistory.data.entitiesWithoutHistory.length > 0 ? ` Historik saknades för: ${ha.importHistory.data.entitiesWithoutHistory.join(', ')}.` : ''}</Alert>}
      {ha.importHistory.isError && <Alert severity="error" sx={{ mt: 2 }}>{ha.importHistory.error.message}</Alert>}
    </Paper>
    <Alert severity="info">Tokens, tokenfragment och tokenlängd visas eller loggas aldrig. Adress och secrets ändras i containerkonfigurationen, inte i databasen.</Alert>
  </Stack>;
}

function EntitiesTab({ draft, setDraft, entities }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void; entities: HomeAssistantEntity[] }) {
  const updateRole = (role: string, selected: HomeAssistantEntity | null, unit: string) => {
    const rest = draft.entities.filter((entity) => entity.role !== role);
    const next: ThermalEntityConfig[] = selected ? [...rest, { id: 0, userId: draft.site.userId, role, entityId: selected.entityId, expectedUnit: unit, enabled: true, minimumValid: null, maximumValid: null, maximumRatePerHour: null }] : rest;
    setDraft({ ...draft, entities: next });
  };
  return <Stack spacing={2}>
    <Box><Typography variant="h5">Entity-mappning</Typography><Typography color="text.secondary">Listan visar livevärde, enhet, ålder och kvalitetsresultat. Obligatoriska entities markeras i readiness.</Typography></Box>
    {roles.map(([role, label, unit]) => {
      const mapping = draft.entities.find((entity) => entity.role === role);
      const value = entities.find((entity) => entity.entityId === mapping?.entityId) ?? null;
      return <Box key={role} sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'minmax(180px,.45fr) minmax(320px,1fr)' }, gap: 2, alignItems: 'center', py: 1.5, borderBottom: 1, borderColor: 'divider' }}>
        <Box><Typography fontWeight={700}>{label}</Typography><Typography variant="caption" color="text.secondary">Förväntad enhet {unit}</Typography></Box>
        <Autocomplete options={entities} value={value} onChange={(_, selected) => updateRole(role, selected, unit)} getOptionLabel={(option) => `${option.friendlyName} · ${option.entityId}`} isOptionEqualToValue={(option, selected) => option.entityId === selected.entityId} renderOption={(props, option) => <Box component="li" {...props} key={option.entityId}><Box sx={{ flex: 1 }}><Typography>{option.friendlyName}</Typography><Typography variant="caption" color="text.secondary">{option.entityId} · {option.state} {option.unit ?? ''} · {formatRelative(option.lastUpdatedUtc)}</Typography></Box><QualityChip quality={option.quality} /></Box>} renderInput={(params) => <TextField {...params} label={`Välj ${label.toLowerCase()}`} helperText={mapping?.entityId ?? 'Inte mappad'} />} />
      </Box>;
    })}
  </Stack>;
}

function RoomsTab({ draft, setDraft, entities }: { draft: ThermalConfig; setDraft: (value: ThermalConfig) => void; entities: HomeAssistantEntity[] }) {
  const temperatureEntities = entities.filter((entity) => entity.unit === '°C' || entity.unit === 'C');
  const update = (index: number, changes: Partial<ThermalRoomConfig>) => setDraft({ ...draft, rooms: draft.rooms.map((room, roomIndex) => roomIndex === index ? { ...room, ...changes } : room) });
  const add = () => setDraft({ ...draft, rooms: [...draft.rooms, { id: 0, userId: draft.site.userId, name: `Rum ${draft.rooms.length + 1}`, entityId: '', targetOffsetC: 0, weight: 1, isCritical: false, enabled: true, minimumValidC: 5, maximumValidC: 35, maximumRateCPerHour: 3 }] });
  return <Stack spacing={2}>
    <Stack direction="row" justifyContent="space-between"><Box><Typography variant="h5">Rum och komfort</Typography><Typography color="text.secondary">Offset flyttar rummets mål; vikt styr husets representativa temperatur.</Typography></Box><Button startIcon={<AddIcon />} onClick={add}>Lägg till rum</Button></Stack>
    {draft.rooms.map((room, index) => <Paper key={`${room.id}-${index}`} variant="outlined" sx={{ p: 2 }}><Stack direction={{ xs: 'column', md: 'row' }} gap={2} alignItems={{ md: 'flex-start' }}><TextField label="Namn" value={room.name} onChange={(event) => update(index, { name: event.target.value })} required /><Autocomplete sx={{ flex: 1, minWidth: 260 }} options={temperatureEntities} value={temperatureEntities.find((entity) => entity.entityId === room.entityId) ?? null} onChange={(_, entity) => update(index, { entityId: entity?.entityId ?? '' })} getOptionLabel={(entity) => `${entity.friendlyName} · ${entity.entityId}`} renderInput={(params) => <TextField {...params} label="Temperaturentity" required error={!room.entityId} helperText={room.entityId || 'Välj en entity'} />} /><TextField type="number" label="Offset °C" value={room.targetOffsetC} onChange={(event) => update(index, { targetOffsetC: Number(event.target.value) })} inputProps={{ step: .1, min: -5, max: 5 }} sx={{ width: 120 }} /><TextField type="number" label="Vikt" value={room.weight} onChange={(event) => update(index, { weight: Number(event.target.value) })} inputProps={{ step: .1, min: 0, max: 100 }} sx={{ width: 110 }} /><FormControlLabel control={<Switch checked={room.isCritical} onChange={(event) => update(index, { isCritical: event.target.checked })} />} label="Kritiskt" /><IconButton aria-label={`Ta bort ${room.name}`} onClick={() => setDraft({ ...draft, rooms: draft.rooms.filter((_, roomIndex) => roomIndex !== index) })}><DeleteOutlineIcon /></IconButton></Stack></Paper>)}
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

function toLocalDateTimeInput(value: Date): string {
  const local = new Date(value.getTime() - value.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}
