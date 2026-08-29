import { useMemo, useState } from 'react';
import { Box, Chip, FormControl, InputLabel, MenuItem, Paper, Select, Slider, Stack, Typography } from '@mui/material';
import VisibilityOutlinedIcon from '@mui/icons-material/VisibilityOutlined';
import TimelineOutlinedIcon from '@mui/icons-material/TimelineOutlined';
import RadioButtonCheckedIcon from '@mui/icons-material/RadioButtonChecked';
import type { DecisionReason, ThermalPlan, ThermalTelemetrySample } from '../../types/api';

export default function ThermalTimeline({ plan, history }: { plan: ThermalPlan; history: ThermalTelemetrySample[] }) {
  const [hours, setHours] = useState(24);
  const [zoom, setZoom] = useState(30);
  const steps = useMemo(() => plan.steps.filter((step) => new Date(step.startUtc).getTime() < Date.now() + hours * 3_600_000), [plan.steps, hours]);
  const width = Math.max(720, steps.length * zoom);
  const prices = steps.map((step) => parseReason(step.decisionReasonJson)?.price ?? 0);
  const predicted = steps.map((step) => parseExpected(step.expectedRoomsJson));
  const actual = history.map((sample) => ({ time: new Date(sample.timestampUtc).getTime(), value: roomAverage(sample.roomTemperaturesJson) })).filter((point): point is { time: number; value: number } => point.value != null);
  const minPrice = Math.min(...prices, 0); const maxPrice = Math.max(...prices, 1);
  const minTemp = Math.min(...predicted.filter(isNumber), ...actual.map((point) => point.value), 18);
  const maxTemp = Math.max(...predicted.filter(isNumber), ...actual.map((point) => point.value), 23);
  const startMs = new Date(steps[0]?.startUtc ?? plan.validFromUtc).getTime();
  const endMs = new Date(steps.length ? steps[steps.length - 1].endUtc : plan.validUntilUtc).getTime();
  const xFor = (time: number) => (time - startMs) / Math.max(1, endMs - startMs) * width;
  const path = (values: Array<number | null>, top: number, height: number, min: number, max: number) => values.map((value, index) => value == null ? null : `${index === 0 ? 'M' : 'L'} ${index * zoom + zoom / 2} ${top + height - (value - min) / Math.max(.01, max - min) * height}`).filter(Boolean).join(' ');
  const actualPath = actual.map((point, index) => `${index === 0 ? 'M' : 'L'} ${xFor(point.time)} ${138 + 62 - (point.value - minTemp) / Math.max(.01, maxTemp - minTemp) * 62}`).join(' ');

  return (
    <Paper variant="outlined" sx={{ overflow: 'hidden' }}>
      <Stack direction={{ xs: 'column', md: 'row' }} gap={2} justifyContent="space-between" alignItems={{ md: 'center' }} sx={{ p: 2, borderBottom: 1, borderColor: 'divider' }}>
        <Stack direction="row" gap={1} flexWrap="wrap">
          <Chip icon={<RadioButtonCheckedIcon />} label="Faktiskt – heldragen" variant="outlined" />
          <Chip icon={<TimelineOutlinedIcon />} label="Prognos – streckad" variant="outlined" />
          {plan.isShadow && <Chip icon={<VisibilityOutlinedIcon />} label="Shadow – prickad markör" color="info" variant="outlined" />}
          <Chip label="DHW – skrafferat fält" color="warning" variant="outlined" />
        </Stack>
        <Stack direction="row" gap={2} alignItems="center">
          <Box sx={{ width: 150 }}><Typography variant="caption">Zoom</Typography><Slider size="small" min={12} max={60} value={zoom} onChange={(_, value) => setZoom(value as number)} aria-label="Zooma tidslinjen" /></Box>
          <FormControl size="small"><InputLabel id="hours-label">Tidsfönster</InputLabel><Select labelId="hours-label" value={hours} label="Tidsfönster" onChange={(event) => setHours(Number(event.target.value))}><MenuItem value={12}>12 timmar</MenuItem><MenuItem value={24}>24 timmar</MenuItem><MenuItem value={48}>48 timmar</MenuItem></Select></FormControl>
        </Stack>
      </Stack>
      <Box sx={{ overflowX: 'auto', cursor: 'grab' }} tabIndex={0} role="region" aria-label="Zoom- och panorerbar värmeplan">
        <svg width={width} height="330" role="img" aria-labelledby="timeline-title timeline-desc">
          <title id="timeline-title">Gemensam värme- och varmvattenplan</title>
          <desc id="timeline-desc">Pris, faktisk och prognostiserad rumstemperatur, LWT-avvikelse samt reserverade varmvattenperioder.</desc>
          <defs><pattern id="dhwHatch" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><line x1="0" y1="0" x2="0" y2="8" stroke="#f6c56f" strokeWidth="3" opacity=".35" /></pattern></defs>
          {[38, 118, 218, 300].map((y) => <line key={y} x1="0" y1={y} x2={width} y2={y} stroke="rgba(157,215,255,.12)" />)}
          {steps.map((step, index) => step.dhwReserved && <rect key={step.id} x={index * zoom} y="38" width={zoom} height="262" fill="url(#dhwHatch)" />)}
          {steps.map((step, index) => index % 16 === 0 && <g key={step.id}><line x1={index * zoom} y1="30" x2={index * zoom} y2="305" stroke="rgba(157,215,255,.13)" /><text x={index * zoom + 5} y="22" fill="#91a5b7" fontSize="11">{formatTime(step.startUtc)}</text></g>)}
          <text x="8" y="54" fill="#91a5b7" fontSize="12">PRIS</text><path d={path(prices, 62, 42, minPrice, maxPrice)} fill="none" stroke="#f6c56f" strokeWidth="2" />
          <text x="8" y="134" fill="#91a5b7" fontSize="12">RUM</text><path d={path(predicted, 138, 62, minTemp, maxTemp)} fill="none" stroke="#9dd7ff" strokeWidth="2.4" strokeDasharray={plan.isShadow ? '2 7' : '10 6'} strokeLinecap="round" /><path d={actualPath} fill="none" stroke="#69d4c0" strokeWidth="2.6" />
          <text x="8" y="234" fill="#91a5b7" fontSize="12">LWT</text><line x1="0" y1="267" x2={width} y2="267" stroke="rgba(255,255,255,.25)" /><path d={path(steps.map((step) => step.desiredLwtDeviationC), 240, 54, -3, 3)} fill="none" stroke="#c6a8ff" strokeWidth="2.4" strokeDasharray={plan.isShadow ? '2 7' : undefined} />
        </svg>
      </Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', px: 2, py: 1.5, borderTop: 1, borderColor: 'divider' }}>
        Tider visas i Europe/Stockholm. Tidslinjen bygger på absoluta tidsstämplar och hanterar därför dygn med 92, 96 och 100 kvartperioder.
      </Typography>
    </Paper>
  );
}

function parseReason(json: string): DecisionReason | null { try { return JSON.parse(json) as DecisionReason; } catch { return null; } }
function parseExpected(json: string): number | null { try { const value = (JSON.parse(json) as { representative?: number | null }).representative; return typeof value === 'number' ? value : null; } catch { return null; } }
function roomAverage(json: string): number | null { try { const values = Object.values(JSON.parse(json) as Record<string, number>).filter(isNumber); return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null; } catch { return null; } }
function isNumber(value: unknown): value is number { return typeof value === 'number' && Number.isFinite(value); }
function formatTime(value: string) { return new Intl.DateTimeFormat('sv-SE', { weekday: 'short', hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Stockholm' }).format(new Date(value)); }
