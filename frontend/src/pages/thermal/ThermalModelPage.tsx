import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Chip, LinearProgress, Paper, Stack, Typography } from '@mui/material';
import ScienceOutlinedIcon from '@mui/icons-material/ScienceOutlined';
import SpeedOutlinedIcon from '@mui/icons-material/SpeedOutlined';
import HomeWorkOutlinedIcon from '@mui/icons-material/HomeWorkOutlined';
import FactCheckOutlinedIcon from '@mui/icons-material/FactCheckOutlined';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { useThermalHistory, useThermalModels } from '../../hooks/thermal/useThermal';
import { MetricCard, PageHeader, formatDateTime } from '../../components/thermal/thermalUi';

export default function ThermalModelPage() {
  const models = useThermalModels();
  const history = useThermalHistory(24);
  const active = models.data?.find((model) => model.isActive && model.modelType === '2R2C');
  const activeCop = models.data?.find((model) => model.isActive && model.modelType === 'COP');
  const metrics = parseJson(active?.metricsJson);
  const parameters = parseJson(active?.parametersJson);
  const copMetrics = parseJson(activeCop?.metricsJson);
  const copParameters = parseJson(activeCop?.parametersJson);
  const roomAdjustments = record(parameters.roomAdjustments);
  const cops = history.data?.map((sample) => sample.cop).filter((value): value is number => value != null) ?? [];
  const averageCop = cops.length ? cops.reduce((sum, value) => sum + value, 0) / cops.length : null;
  const twoHour = number(metrics.twoHourMaeC);
  const day = number(metrics.dayMaeC);
  const copMae = number(copMetrics.mae);

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Transparent, versionshanterad" title="Modell" description="Grey-box-modellen går att förklara: luft, byggnadsmassa, klimatskal och tillförd värme. Vind och sol får bara stanna om de faktiskt förbättrar valideringen." />
      {!models.isLoading && !active && <Alert severity="info">Ingen validerad 2R2C-modell är aktiv ännu. Minst 21 dagars shadowdata krävs innan modellen kan godkännas.</Alert>}
      {!models.isLoading && !activeCop && <Alert severity="info">Ingen separat COP-modell är aktiv ännu. FullActive förblir låst tills effektmätningen är verifierad och COP-modellen klarar valideringen.</Alert>}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 2 }}>
        <MetricCard label="Tvåtimmars-MAE" value={twoHour == null ? '–' : `${twoHour.toFixed(2)} °C`} detail="Krav ≤ 0,30 °C" icon={<FactCheckOutlinedIcon />} accent={twoHour != null && twoHour <= .3 ? '#7bdca7' : '#f6c56f'} />
        <MetricCard label="Dygns-MAE" value={day == null ? '–' : `${day.toFixed(2)} °C`} detail="Krav ≤ 0,60 °C" icon={<ScienceOutlinedIcon />} accent={day != null && day <= .6 ? '#7bdca7' : '#f6c56f'} />
        <MetricCard label="Observerad COP, 24 h" value={averageCop == null ? '–' : averageCop.toFixed(2)} detail={copMae == null ? `${cops.length} giltiga femminutersvärden` : `COP-modell MAE ${copMae.toFixed(2)}`} icon={<SpeedOutlinedIcon />} accent="#9dd7ff" />
        <MetricCard label="Träningsperiod" value={active ? `${Math.round((new Date(active.trainingToUtc).getTime() - new Date(active.trainingFromUtc).getTime()) / 86_400_000)} dygn` : '–'} detail={active ? `Version ${active.id} · ${formatDateTime(active.createdAtUtc)}` : 'Väntar på data'} icon={<HomeWorkOutlinedIcon />} accent="#c6a8ff" />
      </Box>
      {active && (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: '1fr 1fr' }, gap: 2 }}>
          <Paper variant="outlined" sx={{ p: 3 }}>
            <Typography variant="h5">Validering</Typography><Typography color="text.secondary" mb={3}>Undanhållen data används innan en version får bli aktiv.</Typography>
            <MetricProgress label="Två timmar" value={twoHour} limit={.3} />
            <MetricProgress label="Ett dygn" value={day} limit={.6} />
            <Typography variant="body2" color="text.secondary" mt={2}>Konfidensen visas separat från själva prognosen. En modellförändring över 25 % ger en varning om möjlig injustering, termostat- eller byggnadsändring.</Typography>
          </Paper>
          <Paper variant="outlined" sx={{ p: 3 }}>
            <Stack direction="row" justifyContent="space-between"><Box><Typography variant="h5">Fysisk tolkning</Typography><Typography color="text.secondary">Avancerade värden</Typography></Box><Chip label="2R2C" color="primary" variant="outlined" /></Stack>
            <Stack mt={3} spacing={1.4}>
              <Parameter label="Klimatskalets värmeförlust" value={parameters.envelopeConductanceKwPerC} unit="kW/°C" />
              <Parameter label="Byggnadsmassans kapacitet" value={parameters.massCapacityKwhPerC} unit="kWh/°C" />
              <Parameter label="Koppling luft ↔ massa" value={parameters.massCouplingKwPerC} unit="kW/°C" />
              <Parameter label="Inlärd grundkurva" value={parameters.baseCurveSlope} unit="°C/°C ute" />
              <Parameter label="Rumsspecifika profiler" value={Object.keys(roomAdjustments).length} unit="rum" />
            </Stack>
            {Object.keys(roomAdjustments).length > 0 && <Typography variant="body2" color="text.secondary" mt={2}>Varje profil lagrar temperatur-offset, uppskattad tröghet och kvarvarande störningsspridning. Råvärden finns i modellversionens revisionsdata.</Typography>}
            {Object.keys(roomAdjustments).length > 0 && <Accordion sx={{ mt: 2 }}><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Avancerat: rumskalibreringar</Typography></AccordionSummary><AccordionDetails><Stack spacing={2}>{Object.entries(roomAdjustments).map(([entityId, raw]) => { const profile = record(raw); return <Box key={entityId}><Typography fontWeight={700} sx={{ overflowWrap: 'anywhere' }}>{entityId}</Typography><Stack mt={.5} spacing={.5}><Parameter label="Offset" value={profile.offsetC} unit="°C" /><Parameter label="Tröghet" value={profile.inertiaHours} unit="h" /><Parameter label="Störningsspridning" value={profile.disturbanceStdDevC} unit="°C" /><Parameter label="Mätpunkter" value={profile.samples} unit="st" /></Stack></Box>; })}</Stack></AccordionDetails></Accordion>}
          </Paper>
        </Box>
      )}
      {activeCop && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={3}>
            <Box><Typography variant="h5">COP-modell</Typography><Typography color="text.secondary">Elpatronprover filtreras bort. Modellen använder främst köldbärare in, LWT och avgiven belastning.</Typography></Box><Chip label={`Validerings-MAE ${copMae?.toFixed(2) ?? '–'}`} color={copMae != null && copMae <= .5 ? 'success' : 'warning'} variant="outlined" />
          </Stack>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(4, 1fr)' }, gap: 2, mt: 3 }}>
            <Parameter label="Bas-COP" value={copParameters.intercept} unit="" />
            <Parameter label="Köldbärarfaktor" value={copParameters.brineCoefficient} unit="COP/°C" />
            <Parameter label="LWT-faktor" value={copParameters.lwtCoefficient} unit="COP/°C" />
            <Parameter label="Belastningsfaktor" value={copParameters.loadCoefficient} unit="COP/kW" />
          </Box>
        </Paper>
      )}
      <Paper variant="outlined" sx={{ p: 3 }}><Typography variant="h5" mb={2}>Modellversioner</Typography><Stack spacing={1}>{models.data?.map((model) => <Stack key={model.id} direction="row" justifyContent="space-between" sx={{ py: 1, borderBottom: 1, borderColor: 'divider' }}><Box><Typography fontWeight={700}>{model.modelType} · version {model.id}</Typography><Typography variant="body2" color="text.secondary">{formatDateTime(model.trainingFromUtc)} – {formatDateTime(model.trainingToUtc)}</Typography></Box><Chip size="small" label={model.isActive ? 'Aktiv' : 'Arkiverad'} color={model.isActive ? 'success' : 'default'} /></Stack>)}</Stack></Paper>
    </Stack>
  );
}

function MetricProgress({ label, value, limit }: { label: string; value: number | null; limit: number }) { const percentage = value == null ? 0 : Math.min(100, value / limit * 100); return <Box mb={2}><Stack direction="row" justifyContent="space-between"><Typography>{label}</Typography><Typography fontWeight={700}>{value?.toFixed(2) ?? '–'} / {limit.toFixed(2)} °C</Typography></Stack><LinearProgress variant="determinate" value={percentage} color={value != null && value <= limit ? 'success' : 'warning'} sx={{ height: 8, borderRadius: 8, mt: .7 }} /></Box>; }
function Parameter({ label, value, unit }: { label: string; value: unknown; unit: string }) { return <Stack direction="row" justifyContent="space-between"><Typography color="text.secondary">{label}</Typography><Typography fontWeight={700}>{typeof value === 'number' ? value.toFixed(3) : '–'} {unit}</Typography></Stack>; }
function parseJson(json: string | undefined): Record<string, unknown> { if (!json) return {}; try { return JSON.parse(json) as Record<string, unknown>; } catch { return {}; } }
function number(value: unknown): number | null { return typeof value === 'number' ? value : null; }
function record(value: unknown): Record<string, unknown> { return value != null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
