import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Box, Chip, Link, Paper, Stack, Typography } from '@mui/material';
import DeviceThermostatIcon from '@mui/icons-material/DeviceThermostat';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import PauseCircleOutlineIcon from '@mui/icons-material/PauseCircleOutline';
import ReportProblemOutlinedIcon from '@mui/icons-material/ReportProblemOutlined';
import ScheduleIcon from '@mui/icons-material/Schedule';
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined';
import { useThermalConfig, useThermalEvents, useThermalHistory } from '../../hooks/thermal/useThermal';
import { PageHeader, formatDateTime } from '../../components/thermal/thermalUi';
import { describeRoomReading } from './roomTelemetry';

const number = new Intl.NumberFormat('sv-SE', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const readingStyle = {
  Valid: { label: 'Giltig', color: 'success', icon: <CheckCircleOutlineIcon /> },
  Stale: { label: 'Gammal', color: 'warning', icon: <ScheduleIcon /> },
  Invalid: { label: 'Ogiltig', color: 'error', icon: <ReportProblemOutlinedIcon /> },
  Excluded: { label: 'Exkluderad', color: 'error', icon: <ReportProblemOutlinedIcon /> },
  Unavailable: { label: 'Saknas', color: 'default', icon: <InfoOutlinedIcon /> },
  Unknown: { label: 'Status okänd', color: 'warning', icon: <InfoOutlinedIcon /> },
  Imported: { label: 'Importerad historik', color: 'default', icon: <ScheduleIcon /> },
  Disabled: { label: 'Inaktiverad', color: 'default', icon: <PauseCircleOutlineIcon /> },
  FetchError: { label: 'Kan inte verifieras', color: 'warning', icon: <InfoOutlinedIcon /> },
} as const;
const severityStyle = {
  Information: { label: 'Information', color: 'info', icon: <InfoOutlinedIcon /> },
  Warning: { label: 'Varning', color: 'warning', icon: <WarningAmberOutlinedIcon /> },
  ActionRequired: { label: 'Åtgärd krävs', color: 'error', icon: <ReportProblemOutlinedIcon /> },
} as const;

export default function ThermalRoomsPage() {
  const config = useThermalConfig();
  const history = useThermalHistory(6);
  const events = useThermalEvents(100);
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    // Expire displayed readings even when polling fails to deliver a new sample.
    const timer = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(timer);
  }, []);
  const latest = history.data?.[Math.max(0, (history.data?.length ?? 1) - 1)];
  const readings = config.data?.rooms.map(room => ({
    room, reading: describeRoomReading(room, latest, now, history.isError || config.isError),
  })) ?? [];
  const activeRooms = readings.filter(({ room }) => room.enabled);
  const roomEvents = events.data?.filter(event => event.category === 'RoomBalance' || event.category === 'DataQuality')
    .slice().sort((left, right) => (Date.parse(right.timestampUtc) || 0) - (Date.parse(left.timestampUtc) || 0)) ?? [];

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Komfort före pris" title="Rum" description="Aktuella mätvärden och deras kvalitet visas separat från sparade reservvärden och händelsehistorik. En ensam trasig givare får aldrig beordra maximal värme." />
      {config.isLoading && <Typography role="status">Hämtar rumskonfiguration…</Typography>}
      {config.isError && <Alert severity="error">Rumskonfigurationen kunde inte hämtas. Aktuell komfort kan inte verifieras.</Alert>}
      {history.isError && <Alert severity="error">Mätvärdena kunde inte hämtas. Aktuell sensorkvalitet och komfort kan inte verifieras.</Alert>}
      {history.isLoading && <Typography role="status">Hämtar rumsmätningar…</Typography>}
      {!config.isError && config.data?.rooms.length === 0 && <Alert severity="info">Inga rum är konfigurerade. Lägg till rum och välj entities under Inställningar.</Alert>}
      {activeRooms.length > 0 && <Typography color="text.secondary">
        {activeRooms.filter(({ reading }) => reading.current).length} av {activeRooms.length} aktiverade rum har ett aktuellt giltigt mätvärde. Komfortmarginal visas bara för dessa mätningar.
      </Typography>}
      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))', xl: 'repeat(3, minmax(0, 1fr))' }, gap: 2 }}>
        {readings.map(({ room, reading }) => {
          const target = config.data!.site.baseRoomTargetC + room.targetOffsetC;
          const lower = target - config.data!.site.lowerComfortBandC;
          const upper = target + config.data!.site.upperComfortBandC;
          const margin = reading.current && reading.value !== null ? reading.value - lower : null;
          const style = readingStyle[reading.status];
          const titleId = 'room-title-' + room.id;
          return (
            <Paper component="article" aria-labelledby={titleId} key={room.entityId} variant="outlined" sx={{ p: 3, minWidth: 0 }}>
              <Stack spacing={1}>
                <Typography variant="h5" component="h2" id={titleId} sx={{ overflowWrap: 'anywhere' }}>{room.name}</Typography>
                <Typography variant="caption" color="text.secondary" sx={{ overflowWrap: 'anywhere' }}>{room.entityId}</Typography>
                <Stack direction="row" gap={1} flexWrap="wrap">
                  <Chip size="small" variant="outlined" {...style} />
                  {room.isCritical && <Chip size="small" variant="outlined" label="Kritiskt rum" />}
                </Stack>
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 3 }}>
                {reading.kind === 'fallback' ? 'Sparat reservvärde' : reading.current ? 'Senaste giltiga mätvärde' : reading.kind === 'measurement' ? 'Sparat mätvärde' : 'Aktuell temperatur okänd'}
              </Typography>
              <Typography variant="h3" component="p">{reading.value === null ? '–' : number.format(reading.value)} <Box component="span" sx={{ fontSize: '1.2rem', color: 'text.secondary' }}>°C</Box></Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{reading.detail}</Typography>
              {reading.kind === 'fallback' && <Typography variant="body2" sx={{ mt: 1 }}>Det sparade reservvärdet är till för styrningens skydd, inte en bekräftelse på rummets faktiska temperatur.</Typography>}
              <Box component="dl" sx={{ mt: 2, mb: 0, p: 1.5, borderRadius: 2, bgcolor: 'background.default', display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', columnGap: 1, rowGap: 1, overflowWrap: 'anywhere' }}>
                <Typography component="dt" variant="body2">Mål</Typography><Typography component="dd" variant="body2" sx={{ m: 0, textAlign: 'right' }}>{number.format(target)} °C</Typography>
                <Typography component="dt" variant="body2">Komfortintervall</Typography><Typography component="dd" variant="body2" sx={{ m: 0, textAlign: 'right' }}>{number.format(lower)}–{number.format(upper)} °C</Typography>
                <Typography component="dt" variant="body2">Komfortmarginal</Typography><Typography component="dd" variant="body2" fontWeight={750} sx={{ m: 0, textAlign: 'right', color: margin !== null && margin < 0 ? 'warning.main' : 'text.primary' }}>{margin === null ? 'Okänd' : (margin >= 0 ? '+' : '') + number.format(margin) + ' °C'}</Typography>
                <Typography component="dt" variant="body2">Vikt i huset</Typography><Typography component="dd" variant="body2" sx={{ m: 0, textAlign: 'right' }}>{number.format(room.weight)}</Typography>
              </Box>
              {margin !== null && margin < 0 && <Typography variant="body2" color="warning.main" sx={{ mt: 1 }}>Under komfortgränsen – priset får inte gå före rummets komfort.</Typography>}
              <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 2 }}>Senast sparat: <SnapshotTime timestamp={latest?.timestampUtc} /></Typography>
            </Paper>
          );
        })}
      </Box>
      <Paper component="section" aria-labelledby="room-events-title" variant="outlined" sx={{ p: 3 }}>
        <Typography variant="h5" component="h2" id="room-events-title">Rum- och givarhistorik</Typography>
        <Typography color="text.secondary" sx={{ mt: 1 }}>Registrerade händelser, inte en lista över aktiva larm. Nivån gäller när händelsen inträffade. Rumskorten ovan visar senast verifierbara mätdata.</Typography>
        {events.isLoading && <Typography role="status" sx={{ mt: 2 }}>Hämtar händelser…</Typography>}
        {events.isError ? <Alert severity="warning" sx={{ mt: 2 }}>Händelsehistoriken kunde inte hämtas. Det betyder inte att tidigare varningar är åtgärdade.</Alert> : (
          <Box component="ol" sx={{ listStyle: 'none', p: 0, my: 2 }}>
            {roomEvents.slice(0, 5).map(event => (
              <Box component="li" key={event.id} sx={{ py: 2, borderBottom: 1, borderColor: 'divider' }}>
                <Stack direction="row" gap={1.5} alignItems="center" flexWrap="wrap">
                  <Chip size="small" variant="outlined" {...(severityStyle[event.severity] ?? { label: 'Okänd nivå', color: 'default' as const, icon: <InfoOutlinedIcon /> })} />
                  <Typography variant="caption" color="text.secondary">{event.category === 'RoomBalance' ? 'Injustering av rum' : 'Datakvalitet'} · <SnapshotTime timestamp={event.timestampUtc} /></Typography>
                </Stack>
                <Typography sx={{ mt: 1, overflowWrap: 'anywhere' }}>{event.message}</Typography>
              </Box>
            ))}
          </Box>
        )}
        {!events.isLoading && !events.isError && roomEvents.length === 0 && <Typography color="text.secondary" sx={{ my: 2 }}>Inga rum- eller givarhändelser finns i den hämtade historiken.</Typography>}
        <Link component={RouterLink} to="/events">Öppna hela händelseloggen</Link>
      </Paper>
      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack direction="row" gap={2} alignItems="flex-start"><DeviceThermostatIcon color="primary" /><Box><Typography variant="h6" component="h2">Så skyddas komforten</Typography><Typography color="text.secondary">Efter tre felaktiga mätningar exkluderas en givare. Ett kritiskt rum använder högst 30 minuter av sitt senaste giltiga värde och därefter husets representativa temperaturfel. En giltig, ihållande kall givare behåller däremot sitt komfortskydd och ger ett injusteringslarm efter sex timmar.</Typography></Box></Stack>
      </Paper>
    </Stack>
  );
}

function SnapshotTime({ timestamp }: { timestamp: string | undefined }) {
  return timestamp && Number.isFinite(Date.parse(timestamp))
    ? <time dateTime={timestamp} title={timestamp}>{formatDateTime(timestamp)} svensk tid</time>
    : <>Okänd tid</>;
}
