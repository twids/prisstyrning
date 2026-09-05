import { useState } from 'react';
import { Alert, Box, Paper, Stack, ToggleButton, ToggleButtonGroup, Typography } from '@mui/material';
import { LineChart } from '@mui/x-charts/LineChart';
import type { ThermalPlan, ThermalTelemetrySample } from '../../types/api';

const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value);
const timeLabel = (date: Date) => new Intl.DateTimeFormat('sv-SE', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Stockholm', timeZoneName: 'short' }).format(date);
function object(json: string): Record<string, unknown> { try { const value: unknown = JSON.parse(json); return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}; } catch { return {}; } }

export function temperatureRows(history: ThermalTelemetrySample[]) {
  return history.filter((sample) => Number.isFinite(Date.parse(sample.timestampUtc))).sort((a, b) => Date.parse(a.timestampUtc) - Date.parse(b.timestampUtc)).map((sample) => {
    const quality = object(sample.qualityJson);
    const roomQuality = quality.rooms && typeof quality.rooms === 'object' ? quality.rooms as Record<string, { Quality?: unknown; quality?: unknown; Excluded?: boolean; excluded?: boolean }> : {};
    const rooms = Object.entries(object(sample.roomTemperaturesJson)).filter(([id, value]) => {
      const status = roomQuality[id];
      return finite(value) && status && !status.Excluded && !status.excluded && [0, 'Valid'].includes((status.Quality ?? status.quality) as string | number);
    }).map(([, value]) => value as number);
    return { time: new Date(sample.timestampUtc), lwt: finite(sample.leavingWaterTemperatureC) ? sample.leavingWaterTemperatureC : null,
      rwt: finite(sample.returnWaterTemperatureC) ? sample.returnWaterTemperatureC : null,
      outside: finite(sample.outsideTemperatureC) ? sample.outsideTemperatureC : null,
      room: rooms.length ? rooms.reduce((a, b) => a + b, 0) / rooms.length : null,
      deviation: finite(quality.heatingDeviationC) ? quality.heatingDeviationC : null };
  });
}

export default function TemperatureChart({ history, plan }: { history: ThermalTelemetrySample[]; plan?: ThermalPlan | null }) {
  const [hours, setHours] = useState(24);
  const rows = temperatureRows(history).filter((row) => row.time.getTime() >= Date.now() - hours * 3_600_000);
  const latest = rows[rows.length - 1];
  // Explicit null rows break lines over collection outages instead of inventing measurements.
  const data = rows.flatMap((row, index) => index && row.time.getTime() - rows[index - 1].time.getTime() > 600_000
    ? [{ time: new Date(rows[index - 1].time.getTime() + 300_000), lwt: null, rwt: null, outside: null, room: null, deviation: null }, row] : [row]);
  const steps = plan?.steps.filter((step) => Number.isFinite(Date.parse(step.startUtc)) && finite(step.desiredLwtDeviationC)).sort((a, b) => Date.parse(a.startUtc) - Date.parse(b.startUtc)) ?? [];
  const offsets = new Map<number, { time: Date; actual: number | null; proposed: number | null }>();
  data.forEach((row) => offsets.set(row.time.getTime(), { time: row.time, actual: row.deviation, proposed: null }));
  steps.forEach((step) => { const time = Date.parse(step.startUtc); offsets.set(time, { time: new Date(time), actual: offsets.get(time)?.actual ?? null, proposed: step.desiredLwtDeviationC }); });
  const offsetData = [...offsets.values()].sort((a, b) => a.time.getTime() - b.time.getTime());
  return <Paper variant="outlined" sx={{ p: { xs: 1, sm: 2 }, minWidth: 0 }}>
    <Stack spacing={2}>
      <Typography component="h2" variant="h5">Temperaturer och LWT</Typography>
      <ToggleButtonGroup exclusive value={hours} onChange={(_, value: number | null) => value && setHours(value)} aria-label="Historikperiod">
        {[6, 24, 48].map((value) => <ToggleButton key={value} value={value}>{value} timmar</ToggleButton>)}
      </ToggleButtonGroup>
      <Typography>{latest?.lwt != null ? `Senast uppmätt LWT: ${latest.lwt.toFixed(1)} °C · ${timeLabel(latest.time)}` : 'Ingen uppmätt LWT i valt intervall.'}</Typography>
      {!data.length ? <Alert severity="info">Temperaturgrafen visas när telemetri finns. En optimeringsplan behövs inte.</Alert> : <Box aria-label="Uppmätta temperaturer i grader Celsius">
        <LineChart height={310} dataset={data} xAxis={[{ dataKey: 'time', scaleType: 'time', valueFormatter: timeLabel }]} yAxis={[{ label: '°C' }]}
          series={[{ dataKey: 'lwt', label: 'Uppmätt LWT' }, { dataKey: 'rwt', label: 'Uppmätt retur' }, { dataKey: 'room', label: 'Rum, giltigt givarmedel' }, { dataKey: 'outside', label: 'Uppmätt ute' }].map((series) => ({ ...series, showMark: false, connectNulls: false }))} />
      </Box>}
      <Typography component="h3" variant="h6">LWT-avvikelse från Daikins grundkurva</Typography>
      <Typography variant="body2">Heldragen: avläst avvikelse. Streckad: planens förslag, inte ett utfört kommando. Absolut framtida LWT visas inte utan känd grundkurva. Saknade värden lämnas tomma.</Typography>
      {!steps.length && <Alert severity="info">Ingen beräknad LWT-avvikelse finns ännu.</Alert>}
      {plan && Date.parse(plan.validUntilUtc) < Date.now() && <Alert severity="warning">Planen har gått ut. Förslaget visas endast som historik.</Alert>}
      {offsetData.some((row) => row.actual != null || row.proposed != null) && <LineChart height={260} dataset={offsetData} xAxis={[{ dataKey: 'time', scaleType: 'time', valueFormatter: timeLabel }]} yAxis={[{ label: 'Avvikelse °C' }]}
        series={[{ id: 'actual', dataKey: 'actual', label: 'Avläst avvikelse', showMark: true, connectNulls: false }, { id: 'proposed', dataKey: 'proposed', label: plan?.isShadow ? 'Shadow-förslag' : 'Planerat förslag', showMark: true, connectNulls: false, curve: 'stepAfter' }]}
        sx={{ '& .MuiLineElement-series-proposed': { strokeDasharray: '6 4' } }} />}
    </Stack>
  </Paper>;
}
