import { useEffect, useState } from 'react';
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip, Paper, Stack, Typography } from '@mui/material';
import ScienceOutlinedIcon from '@mui/icons-material/ScienceOutlined';
import SpeedOutlinedIcon from '@mui/icons-material/SpeedOutlined';
import HomeWorkOutlinedIcon from '@mui/icons-material/HomeWorkOutlined';
import FactCheckOutlinedIcon from '@mui/icons-material/FactCheckOutlined';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import RefreshIcon from '@mui/icons-material/Refresh';
import { useThermalConfig, useThermalHistory, useThermalModels } from '../../hooks/thermal/useThermal';
import { MetricCard, PageHeader, formatDateTime } from '../../components/thermal/thermalUi';
import { finite, modelEvidence, observedCop, parseRecord, record } from '../../components/thermal/modelEvidence';
import type { ThermalModelVersion } from '../../types/api';

export default function ThermalModelPage() {
  const models = useThermalModels();
  const history = useThermalHistory(24);
  const config = useThermalConfig();
  const [clock, setClock] = useState(Date.now);
  const now = Math.max(clock, Date.now());
  useEffect(() => { const timer = setInterval(() => setClock(Date.now()), 30_000); return () => clearInterval(timer); }, []);
  const rows = models.isError || models.isLoading ? [] : models.data ?? [];
  const ordered = [...rows].sort((left, right) => Date.parse(right.createdAtUtc) - Date.parse(left.createdAtUtc));
  const thermal = ordered.find(model => model.modelType === '2R2C' && model.isActive) ?? ordered.find(model => model.modelType === '2R2C');
  const cop = ordered.find(model => model.modelType === 'COP' && model.isActive) ?? ordered.find(model => model.modelType === 'COP');
  const evidence = modelEvidence(thermal, now);
  const copEvidence = modelEvidence(cop, now);
  const parameters = evidence.scored ? parseRecord(thermal?.parametersJson) : {};
  const copParameters = copEvidence.scored ? parseRecord(cop?.parametersJson) : {};
  const roomAdjustments = record(parameters.roomAdjustments);
  const measured = observedCop(history.isError || history.isLoading ? undefined : history.data, now,
    !config.isError && config.data?.site.heatPumpPowerSignVerified === true);
  const period = thermal ? (Date.parse(thermal.trainingToUtc) - Date.parse(thermal.trainingFromUtc)) / 86_400_000 : NaN;
  const refresh = () => { void models.refetch(); void history.refetch(); void config.refetch(); };

  return (
    <Stack spacing={3}>
      <PageHeader eyebrow="Transparent, versionshanterad" title="Modell"
        description="Husmodellen beskriver luft, byggnadsmassa och värmeförlust. Modellfel mäts på separata data som inte använts för träning eller val av vind- och solpåverkan."
        action={<Button variant="outlined" startIcon={<RefreshIcon />} onClick={refresh} disabled={models.isFetching || history.isFetching || config.isFetching}>Hämta underlag igen</Button>} />
      <Alert severity="info">En validerad modell är inte ett godkännande av aktiv styrning. Lägesguiden kontrollerar även verkliga uppvärmningsdygn, rumskomfort, grundkurva och övriga säkerhetskrav.</Alert>
      {models.isLoading && <Typography role="status">Hämtar modellunderlag…</Typography>}
      {models.isError && <Alert severity="error">Modellunderlaget kunde inte hämtas. Tidigare sparade godkännanden visas inte som aktuella. Försök hämta det igen.</Alert>}
      {!models.isLoading && !models.isError && <>
        <Alert severity={evidence.passed ? 'success' : 'warning'} icon={evidence.passed ? <FactCheckOutlinedIcon /> : undefined}>
          <Typography fontWeight={700}>{evidence.passed ? 'Husmodell: validerad' : 'Husmodell: ej verifierad'}</Typography>
          {thermal ? evidence.reason : 'Ingen modellversion finns ännu. Samla giltiga mätdata och låt den nattliga träningen utvärdera underlaget.'}
          {thermal && <Typography variant="body2" mt={.75}>{sourceSummary(thermal, evidence.sourceVerified)}</Typography>}
        </Alert>
        <Alert severity={copEvidence.passed ? 'success' : 'warning'}>
          <Typography fontWeight={700}>{copEvidence.passed ? 'COP-modell: validerad' : 'COP-modell: ej verifierad'}</Typography>
          {cop ? copEvidence.reason : 'Ingen separat COP-modell finns ännu. Verifiera effektmätningen och samla kompressordata utan elpatron.'}
          {cop && <Typography variant="body2" mt={.75}>{sourceSummary(cop, copEvidence.sourceVerified)}</Typography>}
        </Alert>
      </>}
      {(history.isError || config.isError) && <Alert severity="warning">COP-underlaget eller effektmätningens inställningar kunde inte hämtas. Ingen observerad COP kan verifieras just nu.</Alert>}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 2 }}>
        <MetricCard label="Tvåtimmarsfel (MAE)" value={temperature(evidence.twoHour)} detail={evidence.twoHourWindows ? evidence.twoHourWindows + ' hela tvåtimmarsfönster · krav ≤ 0,30 °C' : 'Hela valideringsfönster saknas'} icon={<FactCheckOutlinedIcon />} />
        <MetricCard label="Dygnsfel (MAE)" value={temperature(evidence.day)} detail={evidence.dayWindows ? evidence.dayWindows + ' hela 24-timmarsfönster · krav ≤ 0,60 °C' : 'Ett kortare fönster räknas inte som ett dygn'} icon={<ScienceOutlinedIcon />} />
        <MetricCard label="Observerad COP, 24 h" value={measured.value == null ? '–' : decimal(measured.value)} detail={measured.count + ' giltiga femminuterspunkter · luckor fylls inte i'} icon={<SpeedOutlinedIcon />} />
        <MetricCard label="Modellens dataperiod" value={thermal && Number.isFinite(period) && period > 0 ? decimal(period, 1) + ' dygn' : '–'} detail={thermal ? 'Version ' + thermal.id + ' · inte antal verifierade uppvärmningsdygn' : 'Väntar på modellunderlag'} icon={<HomeWorkOutlinedIcon />} />
      </Box>
      <Typography variant="body2" color="text.secondary">Observerad COP beräknas som summa avgiven effekt delat med summa eleffekt för giltiga femminuterspunkter. Det är inte ett medel av enskilda COP-tal eller ett intyg på komplett dygnsmätning. Importerade, felaktiga och elpatronpåverkade punkter räknas inte.</Typography>
      {evidence.scored && <Accordion slots={{ heading: 'h2' }}>
        <AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography component="span" variant="h6">Avancerat: husmodell och rumskalibrering</Typography></AccordionSummary>
        <AccordionDetails><Stack spacing={2}>
          <SourceDetails model={thermal} verified={evidence.sourceVerified} />
          <Parameter label="Klimatskalets värmeförlust" value={parameters.envelopeConductanceKwPerC} unit="kW/°C" />
          <Parameter label="Byggnadsmassans kapacitet" value={parameters.massCapacityKwhPerC} unit="kWh/°C" />
          <Parameter label="Koppling luft ↔ massa" value={parameters.massCouplingKwPerC} unit="kW/°C" />
          <Parameter label="Grundkurvans lutning" value={parameters.baseCurveSlope} unit="°C/°C ute" />
          {Object.entries(roomAdjustments).map(([entityId, raw]) => { const profile = record(raw); return <Box key={entityId}>
            <Typography fontWeight={700} sx={{ overflowWrap: 'anywhere' }}>{entityId}</Typography>
            <Parameter label="Offset" value={profile.offsetC} unit="°C" /><Parameter label="Tröghet" value={profile.inertiaHours} unit="h" />
            <Parameter label="Störningsspridning" value={profile.disturbanceStdDevC} unit="°C" /><Parameter label="Mätpunkter" value={profile.samples} unit="st" />
          </Box>; })}
          <Typography variant="body2">Rumskalibrering och vind-/solval ska hållas utanför den slutliga valideringsperioden. En större modellförändring ger en separat varning att granska.</Typography>
        </Stack></AccordionDetails>
      </Accordion>}
      {copEvidence.scored && <Accordion slots={{ heading: 'h2' }}>
        <AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography component="span" variant="h6">Avancerat: COP-modell</Typography></AccordionSummary>
        <AccordionDetails><Stack spacing={1.5}>
          <SourceDetails model={cop} verified={copEvidence.sourceVerified} />
          <Typography>Valideringsfel: {copEvidence.cop == null ? '–' : decimal(copEvidence.cop)} · krav ≤ 0,50</Typography>
          <Parameter label="Bas-COP" value={copParameters.intercept} unit="" /><Parameter label="Köldbärarfaktor" value={copParameters.brineCoefficient} unit="COP/°C" />
          <Parameter label="LWT-faktor" value={copParameters.lwtCoefficient} unit="COP/°C" /><Parameter label="Belastningsfaktor" value={copParameters.loadCoefficient} unit="COP/kW" />
        </Stack></AccordionDetails>
      </Accordion>}
      <Paper component="section" aria-labelledby="model-versions-heading" variant="outlined" sx={{ p: { xs: 2, sm: 3 } }}>
        <Typography id="model-versions-heading" variant="h5" component="h2" mb={2}>Modellversioner</Typography>
        <Typography variant="body2" color="text.secondary" mb={2}>Aktivmarkering är databasens val av modell, inte driftläge. Äldre och underkända kandidater behålls för spårbarhet.</Typography>
        {models.isError ? <Typography>Versionerna kan inte verifieras just nu.</Typography> : <Stack component="ul" sx={{ listStyle: 'none', p: 0, m: 0 }} spacing={2}>
          {ordered.map(model => { const assessment = modelEvidence(model, now); return <Stack component="li" key={model.id} spacing={1} sx={{ borderBottom: 1, borderColor: 'divider', pb: 2, minWidth: 0 }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" alignItems={{ xs: 'flex-start', sm: 'center' }} gap={1}>
              <Typography fontWeight={700}>{model.modelType} · version {model.id}</Typography>
              <Chip size="small" label={(assessment.passed ? 'Validerad' : 'Ej verifierad') + (model.isActive ? ' · aktivmarkering' : '')} color={assessment.passed ? 'success' : 'warning'} variant="outlined" />
            </Stack>
            <Typography variant="body2" color="text.secondary">{date(model.trainingFromUtc)} – {date(model.trainingToUtc)}</Typography>
            <Typography variant="body2" color={assessment.sourceVerified ? 'text.secondary' : 'warning.main'}>
              {assessment.sourceVerified ? `Spårbart källurval · ${integer(model.provenance!.observationCount!)} mätpunkter` : 'Källbevis saknas · modellen måste tränas om'}
            </Typography>
            <Typography variant="body2">{assessment.reason}</Typography>
          </Stack>; })}
          {!models.isLoading && ordered.length === 0 && <Typography component="li">Inga modellversioner har sparats ännu.</Typography>}
        </Stack>}
      </Paper>
    </Stack>
  );
}

function decimal(value: number, digits = 2) { return value.toLocaleString('sv-SE', { minimumFractionDigits: digits, maximumFractionDigits: digits }); }
function integer(value: number) { return value.toLocaleString('sv-SE', { maximumFractionDigits: 0 }); }
function temperature(value: number | null) { return value == null ? '–' : decimal(value) + ' °C'; }
function date(value: string) { return Number.isFinite(Date.parse(value)) ? formatDateTime(value) : 'Okänd tid'; }
function sourceSummary(model: ThermalModelVersion, verified: boolean) {
  return verified
    ? `Träningsunderlag: spårbart · ${integer(model.provenance!.observationCount!)} valda mätpunkter.`
    : 'Träningsunderlag: saknar verifierbart källbevis. En ny nattlig träning krävs.';
}
function SourceDetails({ model, verified }: { model: ThermalModelVersion | undefined; verified: boolean }) {
  if (!model?.provenance || !verified) return null;
  return <Box sx={{ borderLeft: 3, borderColor: 'info.main', pl: 1.5 }}>
    <Typography fontWeight={700}>Versionsbundet träningsunderlag</Typography>
    <Typography variant="body2">Källurval: {date(model.provenance.selectionFromUtc!)} – {date(model.provenance.selectionToUtc!)}</Typography>
    <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>Algoritm: {model.provenance.algorithmVersion} · urvalsregel: {model.provenance.selectionVersion}</Typography>
    <Typography variant="body2">{integer(model.provenance.trainingSamples!)} träningspunkter · {integer(model.provenance.validationSamples!)} valideringspunkter</Typography>
  </Box>;
}
function Parameter({ label, value, unit }: { label: string; value: unknown; unit: string }) {
  const number = finite(value);
  return <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" gap={.5}><Typography color="text.secondary">{label}</Typography><Typography fontWeight={700}>{number == null ? '–' : decimal(number, 3)} {unit}</Typography></Stack>;
}
