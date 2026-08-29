import { Alert, Box, Chip, Paper, Stack, Typography } from '@mui/material';
import DeviceThermostatIcon from '@mui/icons-material/DeviceThermostat';
import GppGoodOutlinedIcon from '@mui/icons-material/GppGoodOutlined';
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined';
import { useThermalConfig, useThermalEvents, useThermalHistory } from '../../hooks/thermal/useThermal';
import { PageHeader, formatRelative } from '../../components/thermal/thermalUi';

export default function ThermalRoomsPage() {
  const config = useThermalConfig();
  const history = useThermalHistory(6);
  const events = useThermalEvents(100);
  const latest = history.data?.[Math.max(0, (history.data?.length ?? 1) - 1)];
  const values = parseMap(latest?.roomTemperaturesJson);
  const roomWarnings = events.data?.filter((event) => event.category === 'RoomBalance' || event.category === 'DataQuality') ?? [];

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Komfort före pris" title="Rum" description="Varje rum har ett tydligt mål, en komfortmarginal och en egen kvalitetsstatus. En ensam trasig givare får aldrig beordra maximal värme." />
      {config.isError && <Alert severity="error">Rumskonfigurationen kunde inte hämtas.</Alert>}
      {roomWarnings.slice(0, 2).map((event) => <Alert key={event.id} severity={event.severity === 'ActionRequired' ? 'error' : 'warning'}>{event.message}</Alert>)}
      {config.data?.rooms.length === 0 && <Alert severity="info">Inga rum är konfigurerade. Lägg till rum och välj entities under Inställningar.</Alert>}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))', xl: 'repeat(3, minmax(0, 1fr))' }, gap: 2 }}>
        {config.data?.rooms.map((room) => {
          const value = values[room.entityId];
          const target = config.data.site.baseRoomTargetC + room.targetOffsetC;
          const margin = value == null ? null : value - (target - config.data.site.lowerComfortBandC);
          const healthy = value != null && latest && Date.now() - new Date(latest.timestampUtc).getTime() <= 10 * 60_000;
          return (
            <Paper key={room.entityId} variant="outlined" sx={{ p: 3, position: 'relative', overflow: 'hidden' }}>
              <Box aria-hidden sx={{ position: 'absolute', inset: '0 0 auto', height: 3, bgcolor: !healthy ? 'error.main' : margin != null && margin < 0 ? 'warning.main' : 'primary.main' }} />
              <Stack direction="row" justifyContent="space-between" alignItems="flex-start">
                <Box><Typography variant="h5">{room.name}</Typography><Typography variant="caption" color="text.secondary">{room.entityId}</Typography></Box>
                <Stack direction="row" gap={.5}>{room.isCritical && <Chip size="small" color="warning" variant="outlined" label="Kritiskt" />}{healthy ? <GppGoodOutlinedIcon color="success" /> : <WarningAmberOutlinedIcon color="error" />}</Stack>
              </Stack>
              <Stack direction="row" alignItems="baseline" gap={1} sx={{ mt: 3 }}><Typography variant="h3">{value == null ? '–' : value.toFixed(1)}</Typography><Typography variant="h6" color="text.secondary">°C</Typography></Stack>
              <Typography color="text.secondary">Mål {target.toFixed(1)} °C · tillåtet {target - config.data.site.lowerComfortBandC}–{target + config.data.site.upperComfortBandC} °C</Typography>
              <Box sx={{ mt: 2, p: 1.5, borderRadius: 2, bgcolor: 'background.default' }}>
                <Stack direction="row" justifyContent="space-between"><Typography variant="body2">Komfortmarginal</Typography><Typography variant="body2" fontWeight={750} color={margin != null && margin < 0 ? 'warning.main' : 'text.primary'}>{margin == null ? 'Okänd' : `${margin >= 0 ? '+' : ''}${margin.toFixed(1)} °C`}</Typography></Stack>
                <Stack direction="row" justifyContent="space-between"><Typography variant="body2">Sensordata</Typography><Typography variant="body2" fontWeight={750}>{healthy ? `Giltig · ${formatRelative(latest?.timestampUtc)}` : 'Saknas eller gammal'}</Typography></Stack>
                <Stack direction="row" justifyContent="space-between"><Typography variant="body2">Vikt i huset</Typography><Typography variant="body2" fontWeight={750}>{room.weight.toFixed(1)}</Typography></Stack>
              </Box>
            </Paper>
          );
        })}
      </Box>
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack direction="row" gap={2} alignItems="flex-start"><DeviceThermostatIcon color="primary" /><Box><Typography variant="h6">Så skyddas komforten</Typography><Typography color="text.secondary">Efter tre felaktiga mätningar exkluderas en givare. Ett kritiskt rum använder högst 30 minuter av sitt senaste giltiga värde och därefter husets representativa temperaturfel. En valid, ihållande kall givare behåller däremot sitt komfortskydd och ger ett injusteringslarm efter sex timmar.</Typography></Box></Stack>
      </Paper>
    </Stack>
  );
}

function parseMap(json: string | undefined): Record<string, number> { if (!json) return {}; try { return JSON.parse(json) as Record<string, number>; } catch { return {}; } }
