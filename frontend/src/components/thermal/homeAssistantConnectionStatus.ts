import type { HomeAssistantConnection, HomeAssistantStatus } from '../../types/api';

// Kept distinct from the component filename on case-insensitive filesystems.

export interface HomeAssistantLiveAssessment {
  label: string;
  detail: string;
  severity: 'info' | 'success' | 'warning';
  verified: boolean;
  showDiagnostics: boolean;
}

export function assessHomeAssistantLive(
  connection: HomeAssistantConnection | null | undefined,
  status: HomeAssistantStatus | undefined,
  checkedAt: number,
  now: number,
  loading = false,
  error = false,
): HomeAssistantLiveAssessment {
  const waiting = (label: string, detail: string, severity: 'info' | 'warning' = 'info'): HomeAssistantLiveAssessment =>
    ({ label, detail, severity, verified: false, showDiagnostics: false });
  if (error) return waiting('Status kunde inte hämtas', 'Gamla uppgifter visas inte som aktuella. Försök uppdatera status igen.', 'warning');
  if (loading || !status) return waiting('Läser anslutningsstatus', 'Väntar på en aktuell status för ditt konto.');
  if (!connection) return waiting('Ingen anslutning sparad', 'Ange Home Assistant-adress och telemetritoken för ditt Daikin-konto.');
  if (!connection.telemetryEnabled) return waiting('Telemetri avstängd', 'Den sparade anslutningen samlar inte data. Legacy-DHW påverkas inte.');
  if (!connection.telemetryTokenConfigured) return waiting('Telemetritoken saknas', 'Spara en telemetritoken innan anslutningen kan starta.', 'warning');
  if (!Number.isFinite(checkedAt) || checkedAt <= 0 || now - checkedAt > 120_000 || checkedAt > now + 30_000)
    return waiting('Statusen är gammal', 'Hämta aktuell status. Senast kända anslutning kan inte bekräftas längre.', 'warning');
  // This is an opaque, microsecond-precision revision, not a JS millisecond date.
  if (!status.configurationUpdatedAtUtc || status.configurationUpdatedAtUtc !== connection.updatedAtUtc)
    return waiting('Kontrollerar sparad ändring', 'Väntar på anslutningsstatus för de senast sparade inställningarna. Tidigare cache används inte.');
  if (!status.configured) return waiting('Telemetri inte tillgänglig', 'Kontrollera den sparade anslutningen. Inga aktuella värden har verifierats.', 'warning');

  const descriptions: Partial<Record<NonNullable<HomeAssistantStatus['phase']>, [string, string, 'info' | 'warning']>> = {
    Reloading: ['Laddar om anslutningen', 'Den tidigare anslutningen är ogiltigförklarad. En ny prenumeration och startbild hämtas automatiskt.', 'info'],
    Connecting: ['Ansluter till Home Assistant', 'Väntar på autentisering och bekräftad prenumeration. Ännu ingen verifierad liveanslutning.', 'info'],
    Synchronizing: ['Läser ny startbild', 'Prenumerationen är bekräftad. Värden blir aktuella först när hela startbilden är inläst.', 'info'],
    Reconnecting: ['Återansluter automatiskt', 'Anslutningen bröts eller kunde inte starta. Ett nytt försök görs med väntetid; tidigare värden är inte aktuella.', 'warning'],
    Disconnected: ['Liveanslutningen är bruten', 'Ingen bekräftad prenumeration finns. Kontrollera anslutningen och hämta status igen.', 'warning'],
  };
  const snapshot = status.lastSnapshotUtc ? Date.parse(status.lastSnapshotUtc) : NaN;
  if (status.phase === 'Connected' && status.connected && Number.isFinite(snapshot) &&
      snapshot >= Date.parse(connection.updatedAtUtc) && snapshot <= now + 30_000) {
    return {
      label: 'Liveansluten', detail: 'Prenumerationen är bekräftad och en fullständig startbild har lästs in för den sparade anslutningen. Varje sensors kvalitet bedöms separat.',
      severity: 'success', verified: true, showDiagnostics: true,
    };
  }
  if (status.phase === 'Connected')
    return waiting('Startbild inte verifierad', 'Serverns anslutningsbesked saknar en verifierad startbild. Inga värden godkänns på den grunden.', 'warning');
  const description = status.phase ? descriptions[status.phase] : undefined;
  if (!description) return waiting('Anslutningsstatus är inte verifierad', 'Statusformatet kunde inte bekräftas. Uppdatera status innan värdena används.', 'warning');
  const [label, detail, severity] = description;
  return { ...waiting(label, detail, severity), showDiagnostics: status.phase === 'Reconnecting' || status.phase === 'Disconnected' };
}
