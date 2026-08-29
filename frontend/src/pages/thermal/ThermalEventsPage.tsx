import { useMemo, useState } from 'react';
import { Alert, Box, Chip, FormControl, InputLabel, MenuItem, Paper, Select, Stack, Typography } from '@mui/material';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import WarningAmberOutlinedIcon from '@mui/icons-material/WarningAmberOutlined';
import ReportProblemOutlinedIcon from '@mui/icons-material/ReportProblemOutlined';
import { useThermalEvents } from '../../hooks/thermal/useThermal';
import { PageHeader, formatDateTime } from '../../components/thermal/thermalUi';

export default function ThermalEventsPage() {
  const events = useThermalEvents(500);
  const [severity, setSeverity] = useState('Alla');
  const filtered = useMemo(() => events.data?.filter((event) => severity === 'Alla' || event.severity === severity) ?? [], [events.data, severity]);
  const counts = { Information: events.data?.filter((event) => event.severity === 'Information').length ?? 0, Warning: events.data?.filter((event) => event.severity === 'Warning').length ?? 0, ActionRequired: events.data?.filter((event) => event.severity === 'ActionRequired').length ?? 0 };

  return (
    <Stack spacing={4}>
      <PageHeader eyebrow="Spårbart beslutsfattande" title="Händelser" description="Varje beslut, kommando, fallback och återhämtning får en begriplig förklaring. Larmnivåer skiljer sådant du bör känna till från sådant som faktiskt kräver åtgärd." />
      <Stack direction="row" gap={1} flexWrap="wrap"><Chip icon={<InfoOutlinedIcon />} label={`Information ${counts.Information}`} variant="outlined" /><Chip icon={<WarningAmberOutlinedIcon />} label={`Varning ${counts.Warning}`} color="warning" variant="outlined" /><Chip icon={<ReportProblemOutlinedIcon />} label={`Åtgärd krävs ${counts.ActionRequired}`} color="error" variant="outlined" /></Stack>
      <Paper variant="outlined">
        <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ p: 2, borderBottom: 1, borderColor: 'divider' }}><Typography variant="h5">Revisionslogg</Typography><FormControl size="small" sx={{ minWidth: 180 }}><InputLabel id="severity-label">Nivå</InputLabel><Select labelId="severity-label" value={severity} label="Nivå" onChange={(event) => setSeverity(event.target.value)}><MenuItem value="Alla">Alla nivåer</MenuItem><MenuItem value="Information">Information</MenuItem><MenuItem value="Warning">Varning</MenuItem><MenuItem value="ActionRequired">Åtgärd krävs</MenuItem></Select></FormControl></Stack>
        {events.isError && <Alert severity="error">Händelserna kunde inte hämtas.</Alert>}
        <Box component="ol" sx={{ listStyle: 'none', m: 0, p: 0 }}>
          {filtered.map((event) => (
            <Box component="li" key={event.id} sx={{ display: 'grid', gridTemplateColumns: { xs: '32px 1fr', md: '32px minmax(160px, .3fr) 1fr auto' }, gap: 2, alignItems: 'start', p: 2.2, borderBottom: 1, borderColor: 'divider' }}>
              <Box sx={{ color: event.severity === 'ActionRequired' ? 'error.main' : event.severity === 'Warning' ? 'warning.main' : 'primary.main' }}>{event.severity === 'ActionRequired' ? <ReportProblemOutlinedIcon /> : event.severity === 'Warning' ? <WarningAmberOutlinedIcon /> : <InfoOutlinedIcon />}</Box>
              <Box><Typography fontWeight={750}>{event.category}</Typography><Typography variant="caption" color="text.secondary">{formatDateTime(event.timestampUtc)}</Typography></Box>
              <Typography>{event.message}</Typography>
              <Chip size="small" label={event.severity === 'ActionRequired' ? 'Åtgärd krävs' : event.severity === 'Warning' ? 'Varning' : 'Info'} color={event.severity === 'ActionRequired' ? 'error' : event.severity === 'Warning' ? 'warning' : 'default'} variant="outlined" sx={{ display: { xs: 'none', md: 'flex' } }} />
            </Box>
          ))}
        </Box>
        {!events.isLoading && filtered.length === 0 && <Typography color="text.secondary" sx={{ p: 3 }}>Inga händelser matchar filtret.</Typography>}
      </Paper>
    </Stack>
  );
}
