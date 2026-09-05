import { Alert, Box, Button, Chip, LinearProgress, Paper, Stack, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import ArrowForwardIcon from '@mui/icons-material/ArrowForward';
import DeviceThermostatIcon from '@mui/icons-material/DeviceThermostat';
import WaterDropOutlinedIcon from '@mui/icons-material/WaterDropOutlined';
import ElectricBoltOutlinedIcon from '@mui/icons-material/ElectricBoltOutlined';
import InsightsOutlinedIcon from '@mui/icons-material/InsightsOutlined';
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined';
import { useThermalConfig, useThermalEvents, useThermalHistory, useThermalReadiness, useThermalStatus } from '../../hooks/thermal/useThermal';
import { MetricCard, PageHeader, formatDateTime, formatRelative, modeLabel } from '../../components/thermal/thermalUi';
import type { ControlMode } from '../../types/api';

export default function ThermalOverviewPage() {
  const status = useThermalStatus();
  const config = useThermalConfig();
  const history = useThermalHistory(6);
  const events = useThermalEvents(5);
  const target: ControlMode = status.data?.mode === 'Legacy' ? 'Shadow' : status.data?.mode === 'Shadow' ? 'LwtActive' : 'FullActive';
  const readiness = useThermalReadiness(target);
  const latest = history.data?.length ? history.data[history.data.length - 1] : undefined;
  const rooms = parseNumbers(latest?.roomTemperaturesJson);
  const roomAverage = rooms.length ? rooms.reduce((sum, value) => sum + value, 0) / rooms.length : null;
  const passed = readiness.data?.checks.filter((check) => check.passed).length ?? 0;
  const total = readiness.data?.checks.length ?? 0;
  const mode = status.data?.mode ?? 'Legacy';

  return (
    <Stack spacing={4}>
      <PageHeader
        eyebrow="Trygg drift först"
        title="Överblick utan överraskningar"
        description="Se vad som styr just nu, varför nästa beslut tas och vad som återstår innan ett aktivt läge är säkert."
        action={<Button component={RouterLink} to="/plan" variant="outlined" endIcon={<ArrowForwardIcon />}>Öppna 48-timmarsplanen</Button>}
      />

      <Paper sx={{ p: { xs: 2.5, md: 4 }, overflow: 'hidden', position: 'relative', background: 'linear-gradient(118deg, rgba(105,212,192,.15), rgba(13,25,37,.9) 52%, rgba(157,215,255,.08))' }}>
        <Box aria-hidden sx={{ position: 'absolute', width: 280, height: 280, borderRadius: '50%', right: -80, top: -130, border: '50px solid rgba(105,212,192,.06)' }} />
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={3} position="relative">
          <Box>
            <Stack direction="row" gap={1} alignItems="center" mb={1}><ShieldOutlinedIcon color="primary" /><Typography variant="overline" fontWeight={800}>Aktuellt driftansvar</Typography></Stack>
            <Typography variant="h3" component="p">{modeLabel[mode]}</Typography>
            <Typography color="text.secondary" sx={{ mt: 1, maxWidth: 660 }}>
              {mode === 'Legacy' && 'Den befintliga, verifierade DHW-styrningen är ensam skrivare. Ny telemetri och optimering kan stängas av utan påverkan.'}
              {mode === 'Shadow' && 'Den nya lösningen mäter och räknar, men inga förslag skickas till värmepumpen eller ONECTA.'}
              {mode === 'LwtActive' && 'Den säkra LWT-regulatorn får skriva begränsad avvikelse. Legacy äger fortfarande allt varmvatten.'}
              {mode === 'FullActive' && 'Den gemensamma planeraren äger atomiskt både LWT- och DHW-skrivrätten.'}
            </Typography>
          </Box>
          <Stack sx={{ minWidth: { md: 310 } }} spacing={1} justifyContent="center">
            <Stack direction="row" justifyContent="space-between"><Typography color="text.secondary">DHW-skrivare</Typography><Typography fontWeight={750}>{status.data?.dhwWriter ?? 'Legacy'}</Typography></Stack>
            <Stack direction="row" justifyContent="space-between"><Typography color="text.secondary">LWT-avvikelse</Typography><Typography fontWeight={750}>{status.data?.currentLwtDeviationC.toFixed(1) ?? '0,0'} °C</Typography></Stack>
            <Stack direction="row" justifyContent="space-between"><Typography color="text.secondary">Nästa styrhändelse</Typography><Typography fontWeight={750}>{formatRelative(status.data?.nextControlEventUtc)}</Typography></Stack>
          </Stack>
        </Stack>
      </Paper>

      {status.data?.fallbackReason && <Alert severity="error" variant="outlined"><strong>Fallback är aktiv.</strong> {status.data.fallbackReason}</Alert>}

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 2 }}>
        <MetricCard label="Representativ rumstemperatur" value={roomAverage == null ? '–' : `${roomAverage.toFixed(1)} °C`} detail={`${rooms.length} aktiva rumsvärden`} icon={<DeviceThermostatIcon />} loading={history.isLoading} />
        <MetricCard label="Varmvattentank" value={latest?.tankTemperatureC == null ? '–' : `${latest.tankTemperatureC.toFixed(1)} °C`} detail={latest?.dhwActive ? 'DHW pågår' : 'Ingen verifierad DHW-drift'} icon={<WaterDropOutlinedIcon />} accent="#9dd7ff" loading={history.isLoading} />
        <MetricCard label="Beräknad COP" value={latest?.cop == null ? '–' : latest.cop.toFixed(2)} detail={config.data?.site.heatPumpPowerSignVerified ? 'Shelly-riktning verifierad' : 'Visas först efter effektverifiering'} icon={<ElectricBoltOutlinedIcon />} accent="#f6c56f" loading={history.isLoading} />
        <MetricCard label="Planens ålder" value={status.data?.planAgeMinutes == null ? '–' : `${status.data.planAgeMinutes} min`} detail={status.data?.emhassEnabled === false ? 'EMHASS-integrationen är avstängd. Legacy styr varmvatten utan EMHASS.' : status.data?.emhassAvailable ? 'EMHASS svarar' : 'EMHASS tillgänglighet är inte verifierad'} icon={<InsightsOutlinedIcon />} accent="#c6a8ff" loading={status.isLoading} />
      </Box>

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1.2fr) minmax(340px, .8fr)' }, gap: 2 }}>
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack direction="row" justifyContent="space-between" alignItems="baseline" gap={2} mb={2}>
            <Box><Typography variant="h5">Vägen till {modeLabel[target]}</Typography><Typography color="text.secondary">Varje krav innehåller en konkret åtgärd.</Typography></Box>
            <Chip label={`${passed}/${total}`} color={readiness.data?.ready ? 'success' : 'default'} />
          </Stack>
          {readiness.isLoading && <LinearProgress />}
          {readiness.data && <LinearProgress variant="determinate" value={total ? passed / total * 100 : 0} color={readiness.data.ready ? 'success' : 'primary'} sx={{ height: 8, borderRadius: 8, mb: 2 }} />}
          <Stack spacing={1}>
            {readiness.data?.checks.slice(0, 5).map((check) => (
              <Stack key={check.key} direction="row" justifyContent="space-between" gap={2} sx={{ py: 1, borderBottom: 1, borderColor: 'divider' }}>
                <Box><Typography fontWeight={650}>{check.requirement}</Typography>{!check.passed && <Typography variant="body2" color="text.secondary">{check.action}</Typography>}</Box>
                <Chip size="small" label={check.passed ? 'Klar' : 'Återstår'} color={check.passed ? 'success' : 'default'} variant="outlined" />
              </Stack>
            ))}
          </Stack>
        </Paper>

        <Paper variant="outlined" sx={{ p: 3 }}>
          <Typography variant="h5">Senaste händelser</Typography>
          <Typography color="text.secondary" mb={2}>Beslut, fallback och återhämtning sparas.</Typography>
          <Stack spacing={1.5}>
            {events.data?.length === 0 && <Typography color="text.secondary">Inga händelser ännu.</Typography>}
            {events.data?.map((event) => (
              <Box key={event.id} sx={{ pl: 1.5, borderLeft: 3, borderColor: event.severity === 'ActionRequired' ? 'error.main' : event.severity === 'Warning' ? 'warning.main' : 'primary.main' }}>
                <Typography variant="body2" fontWeight={700}>{event.message}</Typography>
                <Typography variant="caption" color="text.secondary">{event.category} · {formatDateTime(event.timestampUtc)}</Typography>
              </Box>
            ))}
          </Stack>
          <Button component={RouterLink} to="/events" endIcon={<ArrowForwardIcon />} sx={{ mt: 2 }}>Visa revisionsloggen</Button>
        </Paper>
      </Box>
    </Stack>
  );
}

function parseNumbers(json: string | undefined): number[] {
  if (!json) return [];
  try { return Object.values(JSON.parse(json) as Record<string, number>).filter(Number.isFinite); }
  catch { return []; }
}
