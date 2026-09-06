import { useState } from 'react';
import { Alert, Button, Paper, Stack, Typography } from '@mui/material';
import { apiClient } from '../../api/client';
import { formatDateTime } from './thermalUi';
import type { ShadowLearningVersion } from '../../types/api';

export default function ShadowLearningPanel({ versions, failed, refresh }: { versions: ShadowLearningVersion[]; failed: boolean; refresh: () => Promise<unknown> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const version = versions.find(v => v.id === selected) ?? versions[0];
  const scored = versions.filter(v => v.twoHourErrorC != null);
  const daily = versions.filter(v => v.dayErrorC != null);
  const mean = (values: number[]) => values.length ? (values.reduce((a, b) => a + b, 0) / values.length).toLocaleString('sv-SE', { maximumFractionDigits: 2 }) + ' °C' : 'Väntar på utfall';
  const update = async () => {
    setBusy(true); setError(false);
    try { await apiClient.updateShadowLearning(); await refresh(); } catch { setError(true); } finally { setBusy(false); }
  };
  return <Paper component="section" variant="outlined" aria-labelledby="shadow-learning-heading" sx={{ p: { xs: 2, sm: 3 } }}>
    <Stack spacing={2}>
      <Typography id="shadow-learning-heading" component="h2" variant="h5">Lärande i Shadow</Typography>
      <Alert severity="info">Preliminär temperaturmodell — endast skuggning. Den ändrar aldrig LWT, driftläge eller varmvatten. Du väljer själv när verifierad styrning får aktiveras.</Alert>
      <Typography>Startar med oförändrad rumstemperatur som enkel baslinje. En dämpad temperaturtrend väljs bara om den slår baslinjen på separat historik. Detta är inte en inlärd värmerespons eller en simulering av alternativ LWT.</Typography>
      <Button variant="outlined" disabled={busy} onClick={() => void update()}>Uppdatera skrivfri Shadow-modell</Button>
      <Typography variant="caption">Uppdateras även varje timme i Shadow. Kräver en aktuell giltig rumstemperatur. Ingen träning eller aktivering i andra driftlägen.</Typography>
      {(failed || error) && <Alert severity="error">Shadow-underlaget kunde inte hämtas eller uppdateras. Ett tidigare resultat är inte en aktuell verifiering.</Alert>}
      {!failed && !version && <Typography>Ingen startprognos har sparats ännu. Välj minst ett kritiskt rum och invänta giltig, aktuell rumstemperatur. Saknad värmedata hindrar inte den enkla temperaturbaslinjen.</Typography>}
      {!failed && version && <>
        <Typography fontWeight={700}>{version.learning.stage === 'Persistence' ? 'Preliminär baslinje: oförändrad temperatur' : 'Preliminär modell: dämpad temperaturtrend'}</Typography>
        <Typography>Version {version.id} · utfärdad {formatDateTime(version.issuedAtUtc)} · {version.learning.samples} användbara temperaturpunkter.</Typography>
        <Typography>Observerad utetemperatur: {version.learning.minimumOutsideC ?? '–'} till {version.learning.maximumOutsideC ?? '–'} °C. {version.learning.heatingSamples} punkter med rapporterad husvärme — inte ett kvalitetsgodkännande.</Typography>
        <Alert severity="warning">Värmerespons och LWT-förslag är ännu inte verifierade av denna modell. Mildväderresultat gäller inte automatiskt vid minusgrader. Den detaljerade husmodellen tränas separat när giltig värmedata finns; ingen väntan på 21 dygn krävs för första försöket.</Alert>
        <Typography component="h3" variant="h6">Sparad prognos jämfört med verkligt utfall</Typography>
        <Typography variant="body2">Streckad linje: prognos utfärdad i förväg. Punkter: senare uppmätt, viktad rumstemperatur korrigerad för rumsoffset, under den verkliga styrningen. Ingen simulerad besparing redovisas.</Typography>
        <ForecastGraph version={version} />
        <Typography>Tvåtimmarsfel, senaste {scored.length} observerade prognoser: {mean(scored.map(v => v.twoHourErrorC!))}. Dygnsfel, {daily.length} prognoser: {mean(daily.map(v => v.dayErrorC!))}.</Typography>
        <Typography variant="body2">Äldre versioners prognoser ändras aldrig i efterhand. Fel mellan olika väderperioder är inte en rättvis jämförelse av modellversioner i sig.</Typography>
        <Stack component="ul" sx={{ p: 0, listStyle: 'none' }} spacing={1}>
          {versions.slice(0, 24).map(v => <li key={v.id}><Button onClick={() => setSelected(v.id)} aria-pressed={v.id === version.id}>Visa version {v.id} · {formatDateTime(v.issuedAtUtc)}</Button><Typography variant="body2">2 h: {v.twoHourErrorC == null ? 'väntar på utfall' : v.twoHourErrorC.toFixed(2) + ' °C'} · 24 h: {v.dayErrorC == null ? 'väntar på utfall' : v.dayErrorC.toFixed(2) + ' °C'}</Typography></li>)}
        </Stack>
      </>}
    </Stack>
  </Paper>;
}

function ForecastGraph({ version }: { version: ShadowLearningVersion }) {
  const points = version.learning.forecast;
  const values = points.flatMap(p => [p.predictedC, ...(p.actualC == null ? [] : [p.actualC])]).filter(Number.isFinite);
  if (!values.length) return null;
  const low = Math.min(...values) - .2, high = Math.max(...values) + .2;
  const x = (i: number) => 45 + i / Math.max(1, points.length - 1) * 640;
  const y = (v: number) => 190 - (v - low) / (high - low) * 160;
  return <svg viewBox="0 0 730 225" role="img" aria-label="Temperaturprognos och uppmätt utfall för vald Shadow-version" style={{ width: '100%', maxHeight: 320 }}>
    <title>Temperaturprognos och uppmätt utfall</title>
    <desc>Prognos visas streckad och utfall med punkter. Början är 15 minuter efter utfärdande, slutet 24 timmar efter. Sammanfattade prognosfel finns i text nedanför.</desc>
    <text x="0" y="32" fontSize="12" fill="currentColor">{high.toFixed(1)} °C</text><text x="0" y="190" fontSize="12" fill="currentColor">{low.toFixed(1)} °C</text>
    <polyline fill="none" stroke="#1565c0" strokeWidth="2" strokeDasharray="6 4" points={points.map((p, i) => `${x(i)},${y(p.predictedC)}`).join(' ')} />
    {points.map((p, i) => p.actualC == null ? null : <circle key={p.timestampUtc} cx={x(i)} cy={y(p.actualC)} r="3" fill="#8e24aa"><title>{formatDateTime(p.timestampUtc)}: {p.actualC.toFixed(2)} °C</title></circle>)}
    <text x="45" y="220" fontSize="12" fill="currentColor">+15 min</text><text x="650" y="220" fontSize="12" fill="currentColor">+24 h</text>
  </svg>;
}
