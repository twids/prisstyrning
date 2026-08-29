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

export default function ModeWizard({ open, currentMode, onClose }: { open: boolean; currentMode: ControlMode; onClose: () => void }) {
  const [step, setStep] = useState(0);
  const defaultTarget = useMemo<ControlMode>(() => {
    const index = modes.indexOf(currentMode);
    return modes[Math.min(index + 1, modes.length - 1)];
  }, [currentMode]);
  const [target, setTarget] = useState<ControlMode>(defaultTarget);
  const readiness = useThermalReadiness(target);
  const mutation = useChangeThermalMode();
  const passed = readiness.data?.checks.filter((check) => check.passed).length ?? 0;
  const total = readiness.data?.checks.length ?? 0;

  useEffect(() => {
    if (!open) return;
    setStep(0);
    setTarget(defaultTarget);
  }, [open, defaultTarget]);

  const close = () => {
    setStep(0);
    mutation.reset();
    onClose();
  };

  const activate = async () => {
    await mutation.mutateAsync(target);
    close();
  };

  return (
    <Dialog open={open} onClose={close} maxWidth="md" fullWidth aria-labelledby="mode-dialog-title">
      <DialogTitle id="mode-dialog-title">Guidad ändring av driftläge</DialogTitle>
      <DialogContent>
        <Stepper activeStep={step} sx={{ my: 2 }}>
          {['Välj läge', 'Kontrollera krav', 'Bekräfta ansvar'].map((label) => <Step key={label}><StepLabel>{label}</StepLabel></Step>)}
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

        {step === 1 && (
          <Stack spacing={1.5}>
            {readiness.isLoading && <LinearProgress />}
            {readiness.isError && <Alert severity="error">Readiness kunde inte hämtas: {readiness.error.message}</Alert>}
            {readiness.data && (
              <>
                <Typography>{passed} av {total} krav är godkända.</Typography>
                {readiness.data.checks.map((check) => (
                  <Stack key={check.key} direction="row" gap={1.5} alignItems="flex-start" sx={{ p: 1.5, borderRadius: 2, bgcolor: 'background.default' }}>
                    {check.passed ? <CheckCircleOutlineIcon color="success" /> : <RadioButtonUncheckedIcon color="warning" />}
                    <Box><Typography fontWeight={700}>{check.requirement}</Typography><Typography variant="body2" color="text.secondary">{check.action}</Typography></Box>
                  </Stack>
                ))}
              </>
            )}
          </Stack>
        )}

        {step === 2 && (
          <Stack spacing={2}>
            <Alert severity={target === 'FullActive' ? 'warning' : 'info'}>
              Du ändrar till <strong>{modeLabel[target]}</strong>. Rollback till Legacy är permanent synlig efter aktivering.
            </Alert>
            <Typography>
              {target === 'FullActive'
                ? 'ONECTA-skrivrätten flyttas atomiskt till den gemensamma planeraren. Legacy-jobbet finns kvar och återtar skrivansvaret vid rollback.'
                : target === 'LwtActive'
                  ? 'Systemet får skriva en begränsad P1P2-avvikelse. DHW fortsätter helt oförändrat i Legacy.'
                  : 'Inga nya kommandon skickas till Home Assistant, ONECTA eller värmepumpen.'}
            </Typography>
            {mutation.isError && <Alert severity="error">{mutation.error.message}</Alert>}
          </Stack>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 3 }}>
        <Button onClick={close}>Avbryt</Button>
        {step > 0 && <Button onClick={() => setStep((value) => value - 1)}>Tillbaka</Button>}
        {step < 2 ? (
          <Button variant="contained" onClick={() => setStep((value) => value + 1)} disabled={step === 1 && readiness.data?.ready !== true}>Fortsätt</Button>
        ) : (
          <Button variant="contained" color={target === 'FullActive' ? 'warning' : 'primary'} onClick={activate} disabled={mutation.isPending || readiness.data?.ready !== true}>
            {mutation.isPending ? <CircularProgress size={22} /> : `Aktivera ${modeLabel[target]}`}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
