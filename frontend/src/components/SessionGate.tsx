import { useState, type ReactNode } from 'react';
import { Alert, Box, Button, CircularProgress, Paper, Stack, Typography } from '@mui/material';
import LoginOutlinedIcon from '@mui/icons-material/LoginOutlined';
import HeatPumpOutlinedIcon from '@mui/icons-material/HeatPumpOutlined';
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined';
import RefreshOutlinedIcon from '@mui/icons-material/RefreshOutlined';
import { apiClient } from '../api/client';
import { useSession } from '../hooks/useSession';

export default function SessionGate({ children }: { children: ReactNode }) {
  const session = useSession();
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  if (session.isLoading) {
    return <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center' }}><CircularProgress aria-label="Kontrollerar inloggning" /></Box>;
  }

  if (session.isError) {
    return (
      <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>
        <Paper variant="outlined" sx={{ width: 'min(100%, 540px)', p: { xs: 3, sm: 5 }, borderRadius: 4 }}>
          <Stack spacing={3}>
            <Typography variant="h4" component="h1">Inloggningen kunde inte kontrolleras</Typography>
            <Alert severity="error">Webbsidan kan inte verifiera din inloggning just nu. Inga anläggningsuppgifter visas.</Alert>
            <Typography color="text.secondary">Försök igen när anslutningen fungerar. Kontrollen ändrar inga inställningar eller scheman.</Typography>
            <Button
              fullWidth
              size="large"
              variant="contained"
              startIcon={session.isFetching ? <CircularProgress size={18} color="inherit" /> : <RefreshOutlinedIcon />}
              disabled={session.isFetching}
              aria-busy={session.isFetching}
              onClick={() => { void session.refetch(); }}
            >
              {session.isFetching ? 'Kontrollerar…' : 'Försök igen'}
            </Button>
          </Stack>
        </Paper>
      </Box>
    );
  }

  if (session.data?.authenticated) return children;

  const signIn = async () => {
    setStarting(true);
    setStartError(null);
    try {
      const { url } = await apiClient.startAuth();
      window.location.assign(url);
    } catch {
      // Proxy/identity-provider failures can contain raw HTML or server details.
      // Do not render that response in the public login screen.
      setStartError('Inloggningen kunde inte startas. Försök igen om en stund.');
      setStarting(false);
    }
  };

  return (
    <Box component="main" sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>
      <Paper variant="outlined" sx={{ width: 'min(100%, 540px)', p: { xs: 3, sm: 5 }, borderRadius: 4 }}>
        <Stack spacing={3} alignItems="flex-start">
          <Box sx={{ width: 52, height: 52, borderRadius: 3, display: 'grid', placeItems: 'center', color: '#071019', background: 'linear-gradient(135deg, #69d4c0, #9dd7ff)' }}>
            <HeatPumpOutlinedIcon fontSize="large" />
          </Box>
          <Box>
            <Typography variant="overline" color="primary.main">Prisstyrning</Typography>
            <Typography variant="h3" component="h1" sx={{ fontSize: { xs: '2rem', sm: '2.6rem' } }}>Logga in för att fortsätta</Typography>
            <Typography color="text.secondary" mt={1.5}>Samma verifierade Daikin/ONECTA-konto används för legacy-DHW, värmestyrning och kontots Home Assistant-anslutning.</Typography>
          </Box>
          <Stack direction="row" spacing={1.2} alignItems="flex-start">
            <ShieldOutlinedIcon color="primary" sx={{ mt: .2 }} />
            <Typography variant="body2" color="text.secondary">Ingen sida med anläggningsdata visas innan den signerade sessionen är godkänd. Home Assistant-token returneras aldrig till webbläsaren efter att den sparats.</Typography>
          </Stack>
          {startError && <Alert severity="error" sx={{ width: '100%' }}>{startError}</Alert>}
          <Button fullWidth size="large" variant="contained" startIcon={starting ? <CircularProgress size={18} color="inherit" /> : <LoginOutlinedIcon />} onClick={signIn} disabled={starting}>
            {starting ? 'Öppnar Daikin…' : 'Logga in med Daikin'}
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
}
