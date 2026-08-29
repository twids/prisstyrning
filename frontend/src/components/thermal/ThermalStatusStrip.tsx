import { useState } from 'react';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, Stack, Tooltip, Typography } from '@mui/material';
import BoltIcon from '@mui/icons-material/Bolt';
import CloudDoneIcon from '@mui/icons-material/CloudDone';
import CloudOffIcon from '@mui/icons-material/CloudOff';
import RestoreIcon from '@mui/icons-material/Restore';
import TuneIcon from '@mui/icons-material/Tune';
import { useChangeThermalMode, useThermalStatus } from '../../hooks/thermal/useThermal';
import ModeWizard from './ModeWizard';
import { formatRelative, modeLabel, QualityChip } from './thermalUi';

export default function ThermalStatusStrip() {
  const status = useThermalStatus();
  const rollback = useChangeThermalMode();
  const [wizardOpen, setWizardOpen] = useState(false);
  const [rollbackOpen, setRollbackOpen] = useState(false);
  const data = status.data;

  return (
    <>
      <Box component="section" aria-label="Styrsystemets status" sx={{ borderBottom: 1, borderColor: 'divider', bgcolor: 'rgba(9, 18, 29, .92)', px: { xs: 1.5, md: 3 }, py: 1 }}>
        {status.isError ? (
          <Alert severity="error" variant="outlined" sx={{ py: 0 }}>Status kunde inte hämtas. Legacy påverkas inte.</Alert>
        ) : (
          <Stack direction="row" spacing={1} alignItems="center" tabIndex={0} aria-label="Horisontellt rullbar systemstatus" sx={{ overflowX: 'auto', scrollbarWidth: 'none', pb: .25, '&::-webkit-scrollbar': { display: 'none' } }}>
            <Chip label={data ? modeLabel[data.mode] : 'Laddar…'} color={data?.mode === 'Legacy' ? 'default' : data?.mode === 'Shadow' ? 'info' : 'success'} size="small" />
            {data && <QualityChip quality={data.overallDataQuality} />}
            <Tooltip title="Aktiv skrivare för varmvatten"><Chip size="small" variant="outlined" label={`DHW: ${data?.dhwWriter ?? '–'}`} /></Tooltip>
            <Tooltip title="Senaste giltiga femminuterstelemetri"><Chip size="small" variant="outlined" label={`Telemetri ${formatRelative(data?.lastTelemetryUtc)}`} /></Tooltip>
            <Chip size="small" variant="outlined" icon={data?.emhassAvailable ? <CloudDoneIcon /> : <CloudOffIcon />} label={`EMHASS ${data?.emhassAvailable ? 'klar' : 'nere'}`} color={data?.emhassAvailable ? 'success' : 'default'} />
            <Chip size="small" variant="outlined" label={`Plan ${data?.planAgeMinutes == null ? 'saknas' : `${data.planAgeMinutes} min`}`} color={data?.planAgeMinutes != null && data.planAgeMinutes > 60 ? 'error' : 'default'} />
            <Chip size="small" variant="outlined" icon={<BoltIcon />} label={`LWT ${data?.currentLwtDeviationC.toFixed(1) ?? '0,0'} °C`} />
            {data?.manualOverride && <Chip size="small" color="warning" label="Manuellt läge" />}
            {data?.fallbackReason && <Chip size="small" color="error" label="Fallback aktiv" />}
            <Box sx={{ flex: 1, minWidth: 8 }} />
            {data?.nextControlEventUtc && <Typography variant="caption" whiteSpace="nowrap">Nästa händelse {formatRelative(data.nextControlEventUtc)}</Typography>}
            <Button size="small" startIcon={<TuneIcon />} onClick={() => setWizardOpen(true)} sx={{ whiteSpace: 'nowrap' }}>Byt läge</Button>
            {data && data.mode !== 'Legacy' && (
              <Button size="small" color="warning" variant="outlined" startIcon={<RestoreIcon />} onClick={() => setRollbackOpen(true)} sx={{ whiteSpace: 'nowrap' }}>Rollback</Button>
            )}
          </Stack>
        )}
      </Box>
      {data && <ModeWizard open={wizardOpen} currentMode={data.mode} onClose={() => setWizardOpen(false)} />}
      <Dialog open={rollbackOpen} onClose={() => setRollbackOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle>Återgå säkert till Legacy?</DialogTitle>
        <DialogContent>
          <Alert severity="warning" sx={{ mb: 2 }}>Detta är den permanenta säkerhetsvägen tillbaka.</Alert>
          <Typography>Legacy återtar DHW-skrivrätten, den nya planeraren slutar skriva och LWT-avvikelsen nollställs. Databas, modeller och historik behålls.</Typography>
          {rollback.isError && <Alert severity="error" sx={{ mt: 2 }}>{rollback.error.message}</Alert>}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setRollbackOpen(false)}>Avbryt</Button>
          <Button color="warning" variant="contained" onClick={async () => { await rollback.mutateAsync('Legacy'); setRollbackOpen(false); }} disabled={rollback.isPending}>Återgå till Legacy</Button>
        </DialogActions>
      </Dialog>
    </>
  );
}
