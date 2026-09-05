import { useEffect, useMemo, useState } from 'react';
import {
  Alert, Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControlLabel, LinearProgress, Radio, RadioGroup, Stack, Step, StepLabel, Stepper, Typography,
} from '@mui/material';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import RadioButtonUncheckedIcon from '@mui/icons-material/RadioButtonUnchecked';
import type { ControlMode } from '../../types/api';
import { useChangeThermalMode, useThermalReadiness } from '../../hooks/thermal/useThermal';
import { modeLabel } from './thermalUi';

const modes: ControlMode[] = ['Legacy', 'Shadow', 'LwtActive', 'FullActive'];
const stepLabels = ['Välj läge', 'Kontrollera krav', 'Bekräfta ansvar'];

export default function ModeWizard({ open, currentMode, onClose }: { open: boolean; currentMode: ControlMode; onClose: () => void }) {
  const [step, setStep] = useState(0);
  const defaultTarget = useMemo<ControlMode>(() => {
    const index = modes.indexOf(currentMode);
    return modes[Math.min(index + 1, modes.length - 1)];
  }, [currentMode]);
  const [target, setTarget] = useState<ControlMode>(defaultTarget);
  const readiness = useThermalReadiness(target, open);
  const mutation = useChangeThermalMode();
  const [clock, setClock] = useState(Date.now);
  const now = Math.max(clock, Date.now());
  const busy = readiness.isLoading || readiness.isFetching;
  const currentResult = !busy && !readiness.isError && readiness.data?.targetMode === target &&
    readiness.dataUpdatedAt > 0 && now - readiness.dataUpdatedAt <= 120_000 && readiness.dataUpdatedAt - now <= 30_000;
  const checks = currentResult ? readiness.data?.checks ?? [] : [];
  const passed = checks.filter(check => check.passed).length;
  const total = checks.length;
  const isShadowWarning = (check: typeof checks[number]) => target === 'Shadow' && check.severity === 'Warning' &&
    ['telemetry-fresh', 'telemetry-quality'].includes(check.key);
  const blocking = checks.some(check => !check.passed && !isShadowWarning(check));
  const warnings = checks.filter(check => !check.passed && isShadowWarning(check)).length;
  const allowedTarget = target !== currentMode && (target === 'Legacy' || Math.abs(modes.indexOf(target) - modes.indexOf(currentMode)) === 1);
  // Rollback must remain available during a telemetry outage. The server still
  // verifies safe zeroing and writer handover; this is not a readiness bypass.
  const canProceed = allowedTarget && (target === 'Legacy' || currentResult && readiness.data?.ready === true && total > 0 && !blocking);

  useEffect(() => {
    if (!open) return;
    const timer = window.setInterval(() => setClock(Date.now()), 10_000);
    return () => window.clearInterval(timer);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    setStep(0);
    setTarget(defaultTarget);
  }, [open, defaultTarget]);

  const close = () => {
    if (mutation.isPending) return;
    setStep(0);
    mutation.reset();
    onClose();
  };

  const activate = async () => {
    if (!canProceed || mutation.isPending) return;
    try {
      await mutation.mutateAsync(target);
      close();
    } catch {
      // The mutation renders a fixed error below. Never echo proxy/server text
      // or leave a rejected event-handler promise unobserved.
    }
  };

  return (
    <Dialog open={open} onClose={close} maxWidth="md" fullWidth aria-labelledby="mode-dialog-title">
      <DialogTitle id="mode-dialog-title">Guidad ändring av driftläge</DialogTitle>
      <DialogContent>
        <Typography sx={{ display: { xs: 'block', sm: 'none' }, my: 2 }} role="status">Steg {step + 1} av 3: {stepLabels[step]}</Typography>
        <Stepper activeStep={step} sx={{ display: { xs: 'none', sm: 'flex' }, my: 2 }}>
          {stepLabels.map((label, index) => <Step key={label} completed={index === 0 ? step > 0 : index === 1 && step > 1 && canProceed}><StepLabel>{label}</StepLabel></Step>)}
        </Stepper>

        {step === 0 && (
          <Box>
            <Alert severity="info" sx={{ mb: 2 }}>Nuvarande läge är <strong>{modeLabel[currentMode]}</strong>. Aktiva lägen kan inte hoppas över.</Alert>
            <RadioGroup value={target} onChange={(event) => setTarget(event.target.value as ControlMode)}>
              {modes.map((mode, index) => {
                const currentIndex = modes.indexOf(currentMode);
                const allowed = mode === 'Legacy' || index === currentIndex + 1 || index === currentIndex - 1;
                return (
                  <FormControlLabel
                    key={mode}
                    value={mode}
                    disabled={!allowed || mode === currentMode}
                    control={<Radio />}
                    label={<Box><Typography fontWeight={700}>{modeLabel[mode]}</Typography><Typography variant="body2" color="text.secondary">{
                      mode === 'Legacy' ? 'Endast den beprövade varmvattenstyrningen skriver.' :
                      mode === 'Shadow' ? 'Mäter och räknar, men skriver ingenting.' :
                      mode === 'LwtActive' ? 'Begränsad LWT-korrigering; Legacy äger DHW.' :
                      'Gemensam planerare äger både LWT och DHW.'
                    }</Typography></Box>}
                    sx={{ alignItems: 'flex-start', border: 1, borderColor: 'divider', borderRadius: 2, m: 0, mb: 1, p: 1.5 }}
                  />
                );
              })}
            </RadioGroup>
          </Box>
        )}

        {step >= 1 && (
          <Stack spacing={1.5}>
            {target === 'Legacy' ? <Alert severity="info">Återgång till Legacy kräver inte godkänd telemetri. Servern kontrollerar att LWT kan nollställas och DHW-skrivansvaret återställas säkert.</Alert> : (
              <>
                {busy && <><LinearProgress aria-label="Kontrollerar krav" /><Typography role="status">Kontrollerar kraven på nytt …</Typography></>}
                {!busy && readiness.isError && <Alert severity="error">Kraven kunde inte kontrolleras. Tidigare godkännanden gäller inte; driftläget har inte ändrats här.</Alert>}
                {!busy && !readiness.isError && !currentResult && <Alert severity="warning">Ett aktuellt kontrollresultat för valt driftläge saknas. Hämta kraven igen innan du fortsätter.</Alert>}
                <Button onClick={() => void readiness.refetch()} disabled={busy || mutation.isPending} sx={{ alignSelf: 'flex-start' }}>Kontrollera kraven igen</Button>
              </>
            )}
            {step === 1 && target !== 'Legacy' && currentResult && (
              <>
                <Typography>{passed} av {total} krav är godkända.</Typography>
                {warnings > 0 && <Alert severity="warning">Givarnas aktualitet eller kvalitet är osäker. Det hindrar inte Shadow. Osäkra värden markeras fortsatt och beräkningar kan utebli. Legacy fortsätter styra varmvattnet; Shadow får ingen skrivbehörighet.</Alert>}
                {checks.map((check) => (
                  <Stack key={check.key} direction="row" gap={1.5} alignItems="flex-start" sx={{ p: 1.5, borderRadius: 2, bgcolor: 'background.default' }}>
                    {check.passed ? <CheckCircleOutlineIcon color="success" /> : <RadioButtonUncheckedIcon color="warning" />}
                    <Box><Typography fontWeight={700}>{check.requirement}</Typography><Typography variant="body2">{check.passed ? 'Godkänt' : isShadowWarning(check) ? 'Varning – hindrar inte Shadow' : 'Åtgärd krävs'}</Typography><Typography variant="body2" color="text.secondary">{check.action}</Typography></Box>
                  </Stack>
                ))}
              </>
            )}
          </Stack>
        )}

        {step === 2 && (
          <Stack spacing={2} sx={{ mt: 2 }}>
            {warnings > 0 && <Alert severity="warning">Shadow startas med datavarningar. Osäkra värden blir inte godkända för aktiv styrning. Legacy behåller varmvattenstyrningen.</Alert>}
            <Alert severity={target === 'FullActive' ? 'warning' : 'info'}>
              Valt läge: <strong>{modeLabel[target]}</strong>. Rollback till Legacy är permanent synlig efter aktivering.
            </Alert>
            <Typography>
              {target === 'FullActive'
                ? 'ONECTA-skrivrätten flyttas atomiskt till den gemensamma planeraren. Legacy-jobbet finns kvar och återtar skrivansvaret vid rollback.'
                : target === 'Legacy'
                  ? 'Legacy återtar varmvattenstyrningen. Från ett aktivt läge nollställs LWT-avvikelsen; från FullActive återställs även legacy-schemat i ONECTA. Om nollställningen inte kan verifieras avbryter servern lägesbytet och rapporterar felet.'
                : target === 'LwtActive'
                  ? 'Systemet får skriva en begränsad P1P2-avvikelse. DHW fortsätter helt oförändrat i Legacy.'
                  : 'Inga nya kommandon skickas till Home Assistant, ONECTA eller värmepumpen.'}
            </Typography>
            {mutation.isError && <Alert severity="error">Lägesbytet kunde inte bekräftas. Kontrollera aktuell drift och händelser i översikten innan du försöker igen.</Alert>}
          </Stack>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 3, display: { xs: 'grid', sm: 'flex' }, gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: 1,
        '& > :last-child': { gridColumn: { xs: '1 / -1', sm: 'auto' } }, '& > :not(:first-of-type)': { ml: { xs: 0, sm: 1 } } }}>
        <Button onClick={close} disabled={mutation.isPending}>Avbryt</Button>
        {step > 0 && <Button onClick={() => setStep((value) => value - 1)} disabled={mutation.isPending}>Tillbaka</Button>}
        {step < 2 ? (
          <Button variant="contained" onClick={() => setStep((value) => value + 1)} disabled={!allowedTarget || step === 1 && !canProceed}>Fortsätt</Button>
        ) : (
          <Button variant="contained" color={target === 'FullActive' ? 'warning' : 'primary'} onClick={activate} disabled={mutation.isPending || !canProceed}>
            {mutation.isPending ? <CircularProgress size={22} /> : `Aktivera ${modeLabel[target]}`}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
