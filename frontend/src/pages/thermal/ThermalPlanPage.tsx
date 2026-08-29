import { Alert, Box, Chip, Paper, Stack, Typography } from '@mui/material';
import SavingsOutlinedIcon from '@mui/icons-material/SavingsOutlined';
import TimerOutlinedIcon from '@mui/icons-material/TimerOutlined';
import PsychologyAltOutlinedIcon from '@mui/icons-material/PsychologyAltOutlined';
import { useThermalHistory, useThermalPlan } from '../../hooks/thermal/useThermal';
import { MetricCard, PageHeader, formatDateTime } from '../../components/thermal/thermalUi';
import ThermalTimeline from '../../components/thermal/ThermalTimeline';
import type { DecisionReason } from '../../types/api';

export default function ThermalPlanPage() {
  const plan = useThermalPlan();
  const history = useThermalHistory(48);
  const nextDhw = plan.data?.steps.find((step) => step.dhwReserved && new Date(step.startUtc).getTime() > Date.now());
  const current = plan.data?.steps.find((step) => new Date(step.startUtc).getTime() <= Date.now() && new Date(step.endUtc).getTime() > Date.now());
  const reason = current ? parseReason(current.decisionReasonJson) : null;

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Gemensam tidslinje" title="Plan" description="Husvärme i 15-minuterssteg och hela DHW-cykler i femminuterssteg, sammanförda utan att dubbelboka kompressorn." />
      {plan.isError && <Alert severity="error">Planen kunde inte hämtas: {plan.error.message}</Alert>}
      {!plan.isLoading && !plan.data && <Alert severity="info">Ingen plan finns ännu. I Legacy är det väntat; starta Telemetry Shadow när HA-entities är konfigurerade.</Alert>}
      {plan.data && (
        <>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(3, 1fr)' }, gap: 2 }}>
            <MetricCard label="Beräknad totalkostnad" value={plan.data.objectiveCost == null ? '–' : `${plan.data.objectiveCost.toFixed(2)} kr`} detail={`Horisont till ${formatDateTime(plan.data.validUntilUtc)}`} icon={<SavingsOutlinedIcon />} />
            <MetricCard label="Nästa DHW-reservation" value={nextDhw ? formatDateTime(nextDhw.startUtc).split(' ').slice(-1)[0] : 'Ingen'} detail={nextDhw ? `${nextDhw.dhwMode} · kompressorn reserverad` : 'Ingen cykel i synligt fönster'} icon={<TimerOutlinedIcon />} accent="#f6c56f" />
            <MetricCard label="Modellkonfidens" value={`${Math.round(plan.data.confidence * 100)} %`} detail={`Solver ${plan.data.solverDurationMs} ms`} icon={<PsychologyAltOutlinedIcon />} accent="#c6a8ff" />
          </Box>
          <Paper variant="outlined" sx={{ p: 3 }}>
            <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" gap={2}>
              <Box><Typography variant="overline" color="text.secondary">Varför just nu?</Typography><Typography variant="h5">{reason?.mainReason ?? plan.data.summary}</Typography>{reason?.alternative && <Typography color="text.secondary" sx={{ mt: .5 }}>{reason.alternative}</Typography>}</Box>
              <Stack direction="row" gap={1} alignItems="flex-start"><Chip label={plan.data.isShadow ? 'Shadow-förslag' : 'Aktiv plan'} icon={plan.data.isShadow ? <PsychologyAltOutlinedIcon /> : undefined} color={plan.data.isShadow ? 'info' : 'success'} /><Chip variant="outlined" label={`Skapad ${formatDateTime(plan.data.createdAtUtc)}`} /></Stack>
            </Stack>
          </Paper>
          <ThermalTimeline plan={plan.data} history={history.data ?? []} />
        </>
      )}
    </Stack>
  );
}

function parseReason(json: string): DecisionReason | null { try { return JSON.parse(json) as DecisionReason; } catch { return null; } }
