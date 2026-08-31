import { useEffect, useId, useState } from 'react';
import { Alert, Button, Paper, Stack, Typography } from '@mui/material';
import type { HomeAssistantConnection, HomeAssistantStatus } from '../../types/api';
import { assessHomeAssistantLive } from './homeAssistantConnectionStatus';
import { formatRelative } from './thermalUi';

interface Props {
  connection: HomeAssistantConnection | null;
  status: HomeAssistantStatus | undefined;
  checkedAt: number;
  loading: boolean;
  error: boolean;
  refreshing: boolean;
  refresh: () => void;
}

export default function HomeAssistantLiveStatus({ connection, status, checkedAt, loading, error, refreshing, refresh }: Props) {
  const titleId = useId();
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(timer);
  }, []);
  const assessment = assessHomeAssistantLive(connection, status, checkedAt, Math.max(now, Date.now()), loading, error);
  return <Paper component="section" aria-labelledby={titleId} variant="outlined" sx={{ p: 2.5, minWidth: 0 }}>
    <Stack spacing={2}>
      <Typography id={titleId} component="h3" variant="h6">Liveanslutning</Typography>
      <Alert severity={assessment.severity} role="status">
        <Typography fontWeight={700}>{assessment.label}</Typography>
        <Typography variant="body2">{assessment.detail}</Typography>
      </Alert>
      <Stack spacing={1} component="dl" sx={{ m: 0 }}>
        <StatusRow label="Senast inlästa entities" value={assessment.showDiagnostics ? String(status?.cachedEntities ?? 0) : 'Ej verifierat'} />
        <StatusRow label="Senaste fullständiga startbild" value={assessment.showDiagnostics ? formatRelative(status?.lastSnapshotUtc) : 'Ej verifierat'} />
        <StatusRow label="Senaste aktivitet" value={assessment.showDiagnostics ? formatRelative(status?.lastActivityUtc) : 'Ej verifierat'} />
      </Stack>
      <Button variant="outlined" onClick={refresh} disabled={refreshing}>{refreshing ? 'Hämtar status…' : 'Uppdatera anslutningsstatus'}</Button>
      <Typography variant="caption" color="text.secondary">Uppdateras automatiskt. Knappen hämtar bara status och ändrar inga inställningar.</Typography>
    </Stack>
  </Paper>;
}

function StatusRow({ label, value }: { label: string; value: string }) {
  return <Stack direction={{ xs: 'column', sm: 'row' }} gap={{ xs: 0, sm: 2 }} justifyContent="space-between">
    <Typography component="dt" color="text.secondary">{label}</Typography>
    <Typography component="dd" sx={{ m: 0 }} fontWeight={700}>{value}</Typography>
  </Stack>;
}
