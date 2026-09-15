import { useEffect, useState } from 'react';
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControlLabel, LinearProgress, Stack, Typography } from '@mui/material';
import { useConservativeStartup, useStartConservativeHeating } from '../../hooks/thermal/useConservativeStartup';

export default function ConservativeStartWizard() {
  const [open, setOpen] = useState(false);
  return <>
    <Button variant="outlined" onClick={() => setOpen(true)}>Försiktig start och inlärning</Button>
    {open && <StartupDialog onClose={() => setOpen(false)} />}
  </>;
}

function StartupDialog({ onClose }: { onClose: () => void }) {
  const status = useConservativeStartup();
  const start = useStartConservativeHeating();
  const [curve, setCurve] = useState(false);
  const [fallback, setFallback] = useState(false);
  const [confirm, setConfirm] = useState(false);
  const [now, setNow] = useState(Date.now);
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 10_000); return () => window.clearInterval(timer); }, []);
  const fresh = Math.max(now, Date.now()) - status.dataUpdatedAt <= 60_000 && status.dataUpdatedAt <= Date.now() + 30_000;
  const busy = status.isLoading || status.isFetching || start.isPending;
  const allowed = !busy && !status.isError && fresh && status.data?.readyToCommission === true &&
    status.data.safetyChecks.length > 0 && status.data.safetyChecks.every(check => check.passed) && curve && fallback && confirm;
  const close = () => { if (!start.isPending) onClose(); };
  const activate = async () => {
    if (!allowed) return;
    try { await start.mutateAsync(); } catch { /* Fixed error text below; no raw server response. */ }
  };
  return <Dialog open onClose={close} fullWidth maxWidth="md" aria-labelledby="startup-title">
    <DialogTitle id="startup-title">Försiktig start och inlärning</DialogTitle>
    <DialogContent>
      <Stack spacing={2}>
        <Alert severity="info">En tränad modell och 21 dagars väntan behövs inte för försiktig rumskorrigering.
          Grundkurvan måste fungera och inkopplingen måste verifieras. Legacy fortsätter styra varmvattnet.</Alert>
        <Typography>Regleringen börjar inom ±1 °C, högst ett reglagesteg per 30 minuter. Prisbidrag används bara från en validerad plan och begränsas till ±0,5 °C före avrundning till reglagets steg och totalgränsen. Saknad modell eller plan lämnar rumskorrigeringen tillgänglig.</Typography>
        <Typography component="h2" variant="h6">1. Säker att starta</Typography>
        {status.isLoading && <LinearProgress aria-label="Kontrollerar säker start" />}
        {status.isError && <Alert severity="error">Startkontrollerna kunde inte hämtas. Ingen aktivering är tillåten.</Alert>}
        {status.data?.safetyChecks.map(check => <Box key={check.key}>
          <Typography fontWeight={700}>{check.passed ? '✓ Godkänt' : 'Åtgärd krävs'} — {check.requirement}</Typography>
          <Typography variant="body2">{check.action}</Typography>
        </Box>)}
        <Button onClick={() => void status.refetch()} disabled={busy}>Kontrollera säkerheten igen</Button>
        <Typography component="h2" variant="h6">2. Modellens mognad – separat från säker start</Typography>
        <Typography>Modellen lär sig av faktisk värmedrift. Tre veckor är en utvärderingspunkt, inte ett löfte om optimal styrning. Lite värmebehov ger mindre att lära av. Prognosfel och valideringsresultat visas på sidan Modell; de hämtas inte av denna startkontroll.</Typography>
        {status.data?.optimizationChecks.filter(check => ['model', 'cop-model', 'weather-forecast', 'power-sign'].includes(check.key)).map(check =>
          <Typography key={check.key} variant="body2">{check.passed ? '✓ Godkänt' : 'Återstår för prisoptimering'} — {check.requirement}. {check.action}</Typography>)}
        <Typography component="h2" variant="h6">3. Godkänn ett verkligt inkopplingstest</Typography>
        <Alert severity="warning">Detta är inte ett skrivfritt Shadow-test. Pumpen får först noll, sedan ett positivt reglagesteg på högst 1 °C och därefter noll igen. Varje steg kräver numerisk återkoppling. Vanlig reglering börjar endast om hela testet lyckas. Vid fel försöker systemet nollställa; om det inte kan verifieras krävs manuell kontroll.</Alert>
        <FormControlLabel control={<Checkbox checked={curve} disabled={start.isPending} onChange={(_, value) => setCurve(value)} />}
          label="Jag har bytt från RT till LWT/väderkurveläge och kontrollerat att grundkurvan ger fungerande värme utan appen." />
        <FormControlLabel control={<Checkbox checked={fallback} disabled={start.isPending} onChange={(_, value) => setFallback(value)} />}
          label="Jag har verifierat anläggningens oberoende återställning vid kommunikationsavbrott och kan kontrollera pumpen manuellt. Appen kan inte garantera nollställning utan kontakt." />
        <FormControlLabel control={<Checkbox checked={confirm} disabled={start.isPending} onChange={(_, value) => setConfirm(value)} />}
          label="Jag godkänner skrivtestet och att försiktig LWT-styrning startar när testet lyckas. Legacy behåller varmvattnet." />
        {start.isPending && <Alert severity="info" role="status">Inkoppling och nollställning verifieras. Avbryt inte genom att ändra pumpens inställningar.</Alert>}
        {start.isError && <Alert severity="error">Starten kunde inte bekräftas. Kontrollera driftstatus och händelser; nollställning kan kräva manuell hjälp. Försök inte igen utan att kontrollera pumpen.</Alert>}
        {start.isSuccess && <Alert severity="success">Inkoppling och nollställning verifierades. Försiktig LWT-styrning är aktiverad; Legacy behåller varmvattnet.</Alert>}
      </Stack>
    </DialogContent>
    <DialogActions sx={{ flexWrap: 'wrap', gap: 1 }}>
      <Button onClick={close} disabled={start.isPending}>Stäng</Button>
      <Button variant="contained" color="warning" disabled={!allowed || start.isSuccess} onClick={() => void activate()}>Verifiera inkoppling och starta</Button>
    </DialogActions>
  </Dialog>;
}
