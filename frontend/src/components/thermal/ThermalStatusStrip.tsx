import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, Link, Stack, Tooltip, Typography } from '@mui/material';
import BoltIcon from '@mui/icons-material/Bolt';
import CloudDoneIcon from '@mui/icons-material/CloudDone';
import CloudOffIcon from '@mui/icons-material/CloudOff';
import RestoreIcon from '@mui/icons-material/Restore';
import TuneIcon from '@mui/icons-material/Tune';
import { useChangeThermalMode, useThermalStatus } from '../../hooks/thermal/useThermal';
import ModeWizard from './ModeWizard';
import { formatRelative, modeLabel, QualityChip } from './thermalUi';
import { describeStatusQuality } from './statusQuality';

export default function ThermalStatusStrip() {
  const status = useThermalStatus();
  const rollback = useChangeThermalMode();
  const [wizardOpen, setWizardOpen] = useState(false);
  const [rollbackOpen, setRollbackOpen] = useState(false);
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(timer);
  }, []);
  const data = status.data;
  const quality = data ? describeStatusQuality(data, now) : null;
  const rollbackAction = data && data.mode !== 'Legacy' && (
    <Button size="small" color="warning" variant="outlined" startIcon={<RestoreIcon />} onClick={() => setRollbackOpen(true)} sx={{ whiteSpace: 'nowrap' }}>Rollback</Button>
  );

  return (
    <>
      <Box component="section" aria-label="Styrsystemets status" sx={{ borderBottom: 1, borderColor: 'divider', bgcolor: 'rgba(9, 18, 29, .92)', px: { xs: 1.5, md: 3 }, py: 1 }}>
        {status.isError ? (
          <Stack direction={{ xs: 'column', sm: 'row' }} gap={1} alignItems={{ sm: 'center' }}>
            <Alert severity="error" variant="outlined" sx={{ py: 0, flex: 1 }}>Status kunde inte hämtas. Aktuellt driftläge och datakvalitet kan inte bekräftas.</Alert>
            {rollbackAction}
          </Stack>
        ) : data && quality ? (
          <>
            <Stack direction={{ xs: 'column', sm: 'row' }} gap={1}>
              <Stack direction="row" spacing={1} alignItems="center" tabIndex={0} aria-label="Horisontellt rullbar systemstatus" sx={{ flex: 1, minWidth: 0, overflowX: 'auto', scrollbarWidth: 'none', pb: .25, '&::-webkit-scrollbar': { display: 'none' } }}>
                <Chip label={modeLabel[data.mode]} color={data.mode === 'Legacy' ? 'default' : data.mode === 'Shadow' ? 'info' : 'success'} size="small" />
                <Stack direction="row" gap={.5} alignItems="center" aria-describedby="system-quality-reason">
                  <Typography variant="caption">Datakvalitet</Typography><QualityChip quality={quality.quality} />
                </Stack>
                <Tooltip title="Aktiv skrivare för varmvatten"><Chip size="small" variant="outlined" label={`DHW: ${data.dhwWriter}`} /></Tooltip>
                <Tooltip title="Senast sparade femminutersinsamling, oavsett kvalitet"><Chip size="small" variant="outlined" label={`Insamlat ${formatRelative(data.lastTelemetryUtc)}`} /></Tooltip>
                <Chip size="small" variant="outlined" icon={data.emhassEnabled !== false && data.emhassAvailable ? <CloudDoneIcon /> : <CloudOffIcon />} label={`EMHASS ${data.emhassEnabled === false ? 'avstängd' : data.emhassAvailable ? 'klar' : 'ej verifierad'}`} color={data.emhassEnabled !== false && data.emhassAvailable ? 'success' : 'default'} />
                <Chip size="small" variant="outlined" label={`Plan ${data.planAgeMinutes == null ? 'saknas' : `${data.planAgeMinutes} min`}`} color={data.planAgeMinutes != null && data.planAgeMinutes > 60 ? 'error' : 'default'} />
                <Chip size="small" variant="outlined" icon={<BoltIcon />} label={`LWT ${data.currentLwtDeviationC.toLocaleString('sv-SE', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} °C`} />
                {data.manualOverride && <Chip size="small" color="warning" label="Manuellt läge" />}
                {data.fallbackReason && <Chip size="small" color="error" label="Fallback aktiv" />}
                {data.nextControlEventUtc && <Typography variant="caption" whiteSpace="nowrap">Nästa händelse {formatRelative(data.nextControlEventUtc)}</Typography>}
              </Stack>
              <Stack direction="row" gap={1} justifyContent="flex-end" alignItems="center" sx={{ flexShrink: 0 }}>
                <Button size="small" startIcon={<TuneIcon />} onClick={() => setWizardOpen(true)} sx={{ whiteSpace: 'nowrap' }}>Byt läge</Button>
                {rollbackAction}
              </Stack>
            </Stack>
            <Typography id="system-quality-reason" variant="caption" component="p" color="text.secondary" sx={{ mt: .5, mb: 0, overflowWrap: 'anywhere' }}>
              {quality.reason}{' '}
              {quality.quality !== 'Valid' && <>Kontrollera <Link component={RouterLink} to="/rooms">rum</Link> och <Link component={RouterLink} to="/settings">givarmappning</Link>.{' '}</>}
              Komfort och tillåtelse till aktiv styrning bedöms separat.
            </Typography>
          </>
        ) : (
          <Typography variant="body2" role="status">Hämtar systemstatus…</Typography>
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
