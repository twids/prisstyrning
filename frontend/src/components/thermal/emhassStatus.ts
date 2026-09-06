import type { ThermalStatus } from '../../types/api';

export function describeEmhass(status: ThermalStatus, now = Date.now()) {
  if (status.emhassEnabled === false) return {
    label: 'EMHASS avstängd', connected: false,
    detail: 'EMHASS-integrationen är avstängd. Legacy styr varmvatten utan EMHASS.',
  };
  const connection = status.emhassConnection;
  // Preserve compatibility with an older API, without interpreting solver success as a new probe.
  if (connection == null) return {
    label: status.emhassAvailable ? 'EMHASS klar' : 'EMHASS ej verifierad',
    connected: status.emhassAvailable,
    detail: 'Separat anslutningskontroll saknas i detta API-svar.',
  };
  const checked = connection.checkedUtc ? Date.parse(connection.checkedUtc) : NaN;
  const fresh = Number.isFinite(checked) && checked <= now && now - checked <= 120_000;
  const reachable = fresh ? connection.reachable : null;
  const planning = status.mode === 'Legacy'
    ? 'Ingen optimering körs i Legacy; den befintliga varmvattenstyrningen fortsätter.'
    : status.planCreatedUtc
      ? 'En sparad plan finns; planens ålder och godkännande bedöms separat.'
      : 'Ingen plan finns ännu. Kontrollera modell och planeringsunderlag.';
  return {
    label: reachable === true ? 'EMHASS ansluten' : reachable === false ? 'EMHASS anslutningsfel' : 'EMHASS anslutning okänd',
    connected: reachable === true,
    detail: `${reachable === true ? 'EMHASS svarar på anslutningskontrollen. Det verifierar inte solvern.' : reachable === false ? 'Senaste anslutningskontrollen misslyckades.' : 'En aktuell anslutningskontroll saknas.'} ${planning}`,
  };
}
