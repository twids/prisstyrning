import { Alert, Box, Chip, LinearProgress, Paper, Stack, Typography } from '@mui/material';
import WaterDropOutlinedIcon from '@mui/icons-material/WaterDropOutlined';
import VerifiedOutlinedIcon from '@mui/icons-material/VerifiedOutlined';
import ElectricBoltOutlinedIcon from '@mui/icons-material/ElectricBoltOutlined';
import ScheduleOutlinedIcon from '@mui/icons-material/ScheduleOutlined';
import { useDhwCycles, useThermalConfig, useThermalHistory } from '../../hooks/thermal/useThermal';
import { MetricCard, PageHeader, formatDateTime, formatRelative } from '../../components/thermal/thermalUi';

export default function ThermalDhwPage() {
  const cycles = useDhwCycles();
  const history = useThermalHistory(6);
  const config = useThermalConfig();
  const latest = history.data?.[Math.max(0, (history.data?.length ?? 1) - 1)];
  const current = cycles.data?.find((cycle) => !cycle.actualEndUtc && ['Running', 'Accepted', 'Planned'].includes(cycle.status));
  const hygiene = cycles.data?.find((cycle) => cycle.kind === 'Comfort' && cycle.targetReachedUtc);
  const deadline = hygiene?.targetReachedUtc && config.data
    ? new Date(new Date(hygiene.targetReachedUtc).getTime() + (config.data.site.comfortIntervalDays + config.data.site.comfortFlexibilityDays) * 86_400_000).toISOString()
    : null;
  const progress = current && latest?.tankTemperatureC != null && current.startTemperatureC != null
    ? Math.max(0, Math.min(100, (latest.tankTemperatureC - current.startTemperatureC) / Math.max(.1, current.targetTemperatureC - current.startTemperatureC) * 100))
    : 0;

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Hela cykeln räknas" title="Varmvatten" description="En 30–60 minuters körning behandlas som ett sammanhängande jobb. Varje start jämförs på hela energikostnaden, inte på en ensam billig kvart." />
      {cycles.isError && <Alert severity="error">DHW-historiken kunde inte hämtas.</Alert>}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 2 }}>
        <MetricCard label="Tanktemperatur" value={latest?.tankTemperatureC == null ? '–' : `${latest.tankTemperatureC.toFixed(1)} °C`} detail={latest?.dhwActive ? 'Verifierad DHW-drift' : 'Vilar'} icon={<WaterDropOutlinedIcon />} />
        <MetricCard label="Nästa planerade start" value={current ? formatDateTime(current.plannedStartUtc).split(' ').pop() : 'Ingen'} detail={current ? `${current.kind} · ${current.reservedDurationMinutes} min reserverat` : 'Ingen cykel planerad'} icon={<ScheduleOutlinedIcon />} accent="#9dd7ff" />
        <MetricCard label="Beräknad kostnad" value={current?.predictedCost == null ? '–' : `${current.predictedCost.toFixed(2)} kr`} detail="Inklusive sen COP-försämring" icon={<ElectricBoltOutlinedIcon />} accent="#f6c56f" />
        <MetricCard label="Hygienfrist" value={deadline ? formatRelative(deadline) : 'Ej verifierad'} detail={hygiene ? `Senast uppnådd ${formatDateTime(hygiene.targetReachedUtc)}` : '60 °C behöver verifieras'} icon={<VerifiedOutlinedIcon />} accent="#c6a8ff" />
      </Box>

      {current && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2}>
            <Box><Stack direction="row" gap={1} alignItems="center"><Typography variant="h5">{current.kind}-cykel</Typography><Chip size="small" label={current.status} color={current.status === 'Running' ? 'success' : 'info'} /></Stack><Typography color="text.secondary">Källa {current.source} · start låses 20 minuter i förväg</Typography></Box>
            <Box textAlign={{ md: 'right' }}><Typography variant="body2" color="text.secondary">Förväntad klar</Typography><Typography variant="h6">{formatDateTime(current.estimatedCompletionUtc)}</Typography></Box>
          </Stack>
          <LinearProgress variant="determinate" value={progress} sx={{ mt: 3, height: 10, borderRadius: 10 }} />
          <Stack direction="row" justifyContent="space-between" mt={1}><Typography variant="caption">{current.startTemperatureC?.toFixed(1) ?? '–'} °C</Typography><Typography variant="caption">Mål {current.targetTemperatureC.toFixed(0)} °C · {current.targetVerificationCount}/2 verifieringar</Typography></Stack>
        </Paper>
      )}

      <Alert severity="info" icon={<VerifiedOutlinedIcon />}>
        <strong>Om legionella:</strong> Systemet räknar en 60-graderskörning som genomförd först efter två efterföljande giltiga femminutersmätningar på minst 60 °C. Givaren verifierar temperaturen vid sin mätpunkt—inte hela tankens mikrobiologiska status. Daikins inbyggda säkerhetsfunktion ska lämnas på.
      </Alert>

      <Paper variant="outlined" sx={{ p: 3 }}>
        <Typography variant="h5" mb={2}>Senaste cykler</Typography>
        <Stack spacing={1}>
          {cycles.data?.slice(0, 10).map((cycle) => (
            <Stack key={cycle.id} direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" gap={1} sx={{ py: 1.3, borderBottom: 1, borderColor: 'divider' }}>
              <Box><Typography fontWeight={700}>{cycle.kind} · {formatDateTime(cycle.plannedStartUtc)}</Typography><Typography variant="body2" color="text.secondary">{cycle.source} · {cycle.status} · reserverat {cycle.reservedDurationMinutes} min</Typography></Box>
              <Stack direction="row" gap={1} alignItems="center"><Chip size="small" variant="outlined" label={cycle.actualCost != null ? `Faktiskt ${cycle.actualCost.toFixed(2)} kr` : cycle.predictedCost == null ? 'Kostnad saknas' : `Prognos ${cycle.predictedCost.toFixed(2)} kr`} />{cycle.backupHeaterUsed && <Chip size="small" color="warning" label="Elpatron" />}</Stack>
            </Stack>
          ))}
          {cycles.data?.length === 0 && <Typography color="text.secondary">Inga observerade cykler ännu.</Typography>}
        </Stack>
      </Paper>
    </Stack>
  );
}
