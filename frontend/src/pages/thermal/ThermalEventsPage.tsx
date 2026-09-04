import { useMemo, useState } from 'react';
import { Alert, Box, Button, Chip, FormControl, InputLabel, MenuItem, Paper, Select, Stack, Typography } from '@mui/material';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined';
import ReportProblemOutlinedIcon from '@mui/icons-material/ReportProblemOutlined';
import { useThermalEvents } from '../../hooks/thermal/useThermal';
import { PageHeader, formatDateTime } from '../../components/thermal/thermalUi';

const categoryLabels: Record<string, string> = {
  Optimizer: 'Optimering', ControlMode: 'Driftläge', DataQuality: 'Datakvalitet',
  ModelDrift: 'Modellförändring', SimulatedComfortBreach: 'Beräknat komfortbrott',
  DhwSchedule: 'Varmvattenschema', HistoryImport: 'Historikimport',
};

function eventTime(value: string): string {
  return Number.isFinite(Date.parse(value)) ? formatDateTime(value) : 'Okänd tid';
}

export default function ThermalEventsPage() {
  const events = useThermalEvents(500);
  const [severity, setSeverity] = useState('Alla');
  const filtered = useMemo(() => events.data?.filter((event) => severity === 'Alla' || event.severity === severity) ?? [], [events.data, severity]);
  const counts = { Information: events.data?.filter((event) => event.severity === 'Information').length ?? 0, Warning: events.data?.filter((event) => event.severity === 'Warning').length ?? 0, ActionRequired: events.data?.filter((event) => event.severity === 'ActionRequired').length ?? 0 };

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Spårbart beslutsfattande" title="Händelser" description="Beslut, kommandon, fallback och återhämtning visas som daterad historik. Händelsens nivå gäller när den registrerades, inte nödvändigtvis nu."
        action={<Button variant="outlined" disabled={events.isFetching} onClick={() => void events.refetch()}>Hämta historik igen</Button>} />
      <Typography color="text.secondary">Antalen gäller senast hämtade historik, inte en lista över aktiva larm. Aktuell drift och fallback finns i Översikt.</Typography>
      {!events.isLoading && (events.data?.length ?? 0) > 0 && <Stack direction="row" gap={1} flexWrap="wrap"><Chip icon={<InfoOutlinedIcon />} label={`Information ${counts.Information}`} variant="outlined" /><Chip icon={<WarningAmberOutlinedIcon />} label={`Varning ${counts.Warning}`} color="warning" variant="outlined" /><Chip icon={<ReportProblemOutlinedIcon />} label={`Åtgärd krävs ${counts.ActionRequired}`} color="error" variant="outlined" /></Stack>}
      <Paper component="section" aria-labelledby="event-history-title" variant="outlined">
        <Stack direction={{ xs: 'column', sm: 'row' }} gap={2} justifyContent="space-between" alignItems={{ xs: 'stretch', sm: 'center' }} sx={{ p: 2, borderBottom: 1, borderColor: 'divider' }}><Typography variant="h5" component="h2" id="event-history-title">Revisionslogg</Typography><FormControl size="small" sx={{ minWidth: 180 }}><InputLabel id="severity-label">Nivå</InputLabel><Select labelId="severity-label" value={severity} label="Nivå" onChange={(event) => setSeverity(event.target.value)}><MenuItem value="Alla">Alla nivåer</MenuItem><MenuItem value="Information">Information</MenuItem><MenuItem value="Warning">Varning</MenuItem><MenuItem value="ActionRequired">Åtgärd krävs</MenuItem></Select></FormControl></Stack>
        {events.isLoading && <Typography role="status" sx={{ p: 3 }}>Hämtar händelser…</Typography>}
        {events.isError && <Alert severity="error">{events.data?.length ? 'Historiken kunde inte uppdateras. Visar tidigare hämtade händelser.' : 'Händelserna kunde inte hämtas.'} Försök hämta historiken igen. Det ändrar inga inställningar och skickar inga styrkommandon.</Alert>}
        <Box component="ol" aria-label="Sparade händelser" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {filtered.map((event) => (
            <Box component="li" key={event.id} sx={{ display: 'grid', gridTemplateColumns: { xs: '32px 1fr', md: '32px minmax(160px, .3fr) 1fr auto' }, gap: 2, alignItems: 'start', p: 2.2, borderBottom: 1, borderColor: 'divider' }}>
              <Box sx={{ color: event.severity === 'ActionRequired' ? 'error.main' : event.severity === 'Warning' ? 'warning.main' : 'primary.main' }}>{event.severity === 'ActionRequired' ? <ReportProblemOutlinedIcon /> : event.severity === 'Warning' ? <WarningAmberOutlinedIcon /> : <InfoOutlinedIcon />}</Box>
              <Box><Typography fontWeight={750}>{categoryLabels[event.category] ?? event.category}</Typography><Typography component="time" dateTime={Number.isFinite(Date.parse(event.timestampUtc)) ? event.timestampUtc : undefined} variant="caption" color="text.secondary">{eventTime(event.timestampUtc)}{Number.isFinite(Date.parse(event.timestampUtc)) ? ' · svensk tid' : ''}</Typography></Box>
              <Typography sx={{ gridColumn: { xs: '2', md: 'auto' }, overflowWrap: 'anywhere', minWidth: 0 }}>{event.message}</Typography>
              <Chip size="small" label={event.severity === 'ActionRequired' ? 'Åtgärd krävs' : event.severity === 'Warning' ? 'Varning' : 'Information'} color={event.severity === 'ActionRequired' ? 'error' : event.severity === 'Warning' ? 'warning' : 'default'} variant="outlined" sx={{ gridColumn: { xs: '2', md: 'auto' }, justifySelf: 'start' }} />
            </Box>
          ))}
        </Box>
        {!events.isLoading && !events.isError && filtered.length === 0 && <Typography color="text.secondary" sx={{ p: 3 }}>Inga händelser matchar filtret.</Typography>}
      </Paper>
    </Stack>
  );
}
