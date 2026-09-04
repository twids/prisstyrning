import { useState } from 'react';
import {
  Container,
  Paper,
  Typography,
  TextField,
  Button,
  Alert,
  AlertTitle,
  Snackbar,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Switch,
  Chip,
  Tooltip,
  CircularProgress,
  Stack,
  Box,
} from '@mui/material';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import CancelIcon from '@mui/icons-material/Cancel';
import DeleteIcon from '@mui/icons-material/Delete';
import IconButton from '@mui/material/IconButton';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import { useFormatters } from '../context/TimezoneContext';
import type { AdminUser } from '../types/api';

export default function AdminPage() {
  const queryClient = useQueryClient();
  const { formatDateTime } = useFormatters();
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState<string | null>(null);
  const [snackbar, setSnackbar] = useState<{ open: boolean; message: string; severity: 'error' | 'success' }>({ open: false, message: '', severity: 'error' });
  const [pendingToggles, setPendingToggles] = useState<Set<string>>(new Set());

  const statusQuery = useQuery({
    queryKey: ['admin-status'],
    queryFn: () => apiClient.getAdminStatus(),
  });

  const isAdmin = statusQuery.data?.isAdmin === true && !statusQuery.isError;

  const usersQuery = useQuery({
    queryKey: ['admin-users'],
    queryFn: () => apiClient.getAdminUsers(),
    enabled: isAdmin,
  });

  const loginMutation = useMutation({
    mutationFn: (pw: string) => apiClient.adminLogin(pw),
    onSuccess: () => {
      setLoginError(null);
      setPassword('');
      queryClient.invalidateQueries({ queryKey: ['admin-status'] });
    },
    onError: () => {
      setLoginError('Administratörsinloggningen misslyckades. Kontrollera lösenordet och försök igen.');
    },
  });

  const toggleAdminMutation = useMutation<{ granted?: boolean; revoked?: boolean; userId: string }, Error, AdminUser>({
    mutationFn: (user) =>
      user.isAdmin ? apiClient.revokeAdmin(user.userId) : apiClient.grantAdmin(user.userId),
    onMutate: (user) => {
      setPendingToggles((prev) => new Set(prev).add(`admin-${user.userId}`));
    },
    onSettled: (_data, _err, user) => {
      if (user) {
        setPendingToggles((prev) => {
          const next = new Set(prev);
          next.delete(`admin-${user.userId}`);
          return next;
        });
      }
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: () => {
      setSnackbar({ open: true, message: 'Ändringen av adminbehörighet kunde inte bekräftas. Kontrollera aktuell behörighet i användarlistan innan du försöker igen.', severity: 'error' });
    },
  });

  const toggleHangfireMutation = useMutation<{ granted?: boolean; revoked?: boolean; userId: string }, Error, AdminUser>({
    mutationFn: (user) =>
      user.hasHangfireAccess ? apiClient.revokeHangfire(user.userId) : apiClient.grantHangfire(user.userId),
    onMutate: (user) => {
      setPendingToggles((prev) => new Set(prev).add(`hangfire-${user.userId}`));
    },
    onSettled: (_data, _err, user) => {
      if (user) {
        setPendingToggles((prev) => {
          const next = new Set(prev);
          next.delete(`hangfire-${user.userId}`);
          return next;
        });
      }
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: () => {
      setSnackbar({ open: true, message: 'Ändringen av Hangfire-behörighet kunde inte bekräftas. Kontrollera aktuell behörighet i användarlistan innan du försöker igen.', severity: 'error' });
    },
  });

  const handleLogin = (e: React.FormEvent) => {
    e.preventDefault();
    if (!password.trim()) return;
    loginMutation.mutate(password);
  };

  const toUtcIso = (date: Date | string | number) => {
    const parsed = new Date(date);
    return Number.isNaN(parsed.getTime()) ? String(date) : parsed.toISOString();
  };

  if (statusQuery.isLoading) {
    return (
      <Container maxWidth="lg" sx={{ py: 4, display: 'flex', justifyContent: 'center' }}>
        <CircularProgress aria-label="Kontrollerar adminbehörighet" />
      </Container>
    );
  }

  if (statusQuery.isError) {
    return (
      <Container maxWidth="sm" sx={{ py: 4 }}>
        <Typography component="h1" variant="h4" gutterBottom>Admin</Typography>
        <Alert severity="error" sx={{ mb: 2 }}>
          Adminbehörigheten kunde inte kontrolleras. Användarlistan och dess åtgärder visas inte förrän kontrollen lyckas.
        </Alert>
        <Button variant="outlined" onClick={() => statusQuery.refetch()} disabled={statusQuery.isFetching}>
          Försök igen
        </Button>
      </Container>
    );
  }

  // Login form
  if (!isAdmin) {
    return (
      <Container maxWidth="sm" sx={{ py: 4 }}>
        <Paper sx={{ p: 4 }}>
          <Typography component="h1" variant="h4" gutterBottom>
            Admin
          </Typography>
          <form onSubmit={handleLogin}>
            <Stack spacing={2}>
              <TextField
                label="Lösenord"
                type="password"
                fullWidth
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoFocus
              />
              {loginError && <Alert severity="error">{loginError}</Alert>}
              <Button
                type="submit"
                variant="contained"
                disabled={loginMutation.isPending || !password.trim()}
                startIcon={loginMutation.isPending ? <CircularProgress size={18} /> : undefined}
              >
                Logga in
              </Button>
            </Stack>
          </form>
        </Paper>
      </Container>
    );
  }

  // Admin: User table
  const users = usersQuery.data?.users ?? [];

  return (
    <Container maxWidth="lg" sx={{ py: 4 }}>
      <Typography component="h1" variant="h4" gutterBottom sx={{ fontSize: { xs: '1.5rem', md: '2rem' } }}>
        Användare
      </Typography>

      <Alert severity="info" role="note" id="account-deletion-unavailable" sx={{ mb: 2 }}>
        <AlertTitle>Kontoradering är tillfälligt spärrad</AlertTitle>
        Säker kontoradering måste hantera pågående styrning, inloggningar, integrationer och historik tillsammans.
        Du kan därför inte radera konton här. Inga konton har ändrats av spärren och befintlig schemastyrning fortsätter.
      </Alert>

      {usersQuery.isLoading && (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
          <CircularProgress aria-label="Hämtar användare" />
        </Box>
      )}

      {usersQuery.error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          Användarlistan kunde inte hämtas. Sparade uppgifter och åtgärder döljs tills listan har kunnat uppdateras.
          <Button onClick={() => usersQuery.refetch()} disabled={usersQuery.isFetching} sx={{ ml: 1 }}>
            Försök igen
          </Button>
        </Alert>
      )}

      {usersQuery.data && !usersQuery.isError && (
        <TableContainer component={Paper} role="region" aria-label="Användarlista" tabIndex={0}>
          <Table size="small" aria-label="Användare och behörigheter">
            <TableHead>
              <TableRow>
                <TableCell>Användare</TableCell>
                <TableCell>Zon</TableCell>
                <TableCell>Inställningar</TableCell>
                <TableCell>Daikin</TableCell>
                <TableCell>Daikin-identitet</TableCell>
                <TableCell>Schema</TableCell>
                <TableCell>Admin</TableCell>
                <TableCell>Hangfire</TableCell>
                <TableCell>Skapad</TableCell>
                <TableCell>Åtgärd</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {users.map((user) => (
                <TableRow key={user.userId} sx={user.isCurrentUser ? { bgcolor: 'action.selected' } : undefined}>
                  <TableCell>
                    <Stack direction="row" alignItems="center" spacing={1}>
                      <Tooltip title={user.userId}>
                        <Typography variant="body2" sx={{ fontFamily: 'monospace', cursor: 'pointer', userSelect: 'all' }}>
                          {user.userId}
                        </Typography>
                      </Tooltip>
                      {user.isCurrentUser && <Chip label="Du" color="primary" size="small" />}
                    </Stack>
                  </TableCell>
                  <TableCell>{user.zone || '—'}</TableCell>
                  <TableCell>
                    <Typography variant="body2" noWrap>
                      {user.settings.ComfortHours}h, {(user.settings.TurnOffPercentile * 100).toFixed(0)}%
                    </Typography>
                  </TableCell>
                  <TableCell>
                    {user.daikinAuthorized ? (
                      <Tooltip describeChild title={user.daikinExpiresAtUtc ? `Utgår: ${formatDateTime(user.daikinExpiresAtUtc)}` : 'Auktoriserad'}>
                        <CheckCircleIcon color="success" fontSize="small" titleAccess="Daikin-auktorisering finns" />
                      </Tooltip>
                    ) : (
                      <Tooltip describeChild title="Ej auktoriserad">
                        <CancelIcon color="error" fontSize="small" titleAccess="Daikin-auktorisering saknas" />
                      </Tooltip>
                    )}
                  </TableCell>
                  <TableCell>
                    {user.daikinSubject ? (
                      <Tooltip title={user.daikinSubject}>
                        <Typography variant="body2" sx={{ fontFamily: 'monospace', cursor: 'pointer', userSelect: 'all', maxWidth: 120, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {user.daikinSubject}
                        </Typography>
                      </Tooltip>
                    ) : (
                      <Typography variant="body2" color="text.secondary">—</Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    {user.hasScheduleHistory ? (
                      <Tooltip title={user.lastScheduleDate ? `Senast: ${formatDateTime(user.lastScheduleDate)}` : ''}>
                        <Typography variant="body2">{user.scheduleCount} st</Typography>
                      </Tooltip>
                    ) : (
                      '—'
                    )}
                  </TableCell>
                  <TableCell>
                    {pendingToggles.has(`admin-${user.userId}`) ? (
                      <CircularProgress size={20} />
                    ) : (
                      <Switch
                        checked={user.isAdmin}
                        disabled={user.isCurrentUser || usersQuery.isFetching || statusQuery.isFetching}
                        onChange={() => toggleAdminMutation.mutate(user)}
                        slotProps={{ input: { role: 'switch', 'aria-label': `Adminbehörighet för ${user.userId}` } }}
                        size="small"
                      />
                    )}
                  </TableCell>
                  <TableCell>
                    {pendingToggles.has(`hangfire-${user.userId}`) ? (
                      <CircularProgress size={20} />
                    ) : (
                      <Switch
                        checked={user.hasHangfireAccess}
                        disabled={usersQuery.isFetching || statusQuery.isFetching}
                        onChange={() => toggleHangfireMutation.mutate(user)}
                        slotProps={{ input: { role: 'switch', 'aria-label': `Hangfire-behörighet för ${user.userId}` } }}
                        size="small"
                      />
                    )}
                  </TableCell>
                  <TableCell>
                    {user.createdAt ? (
                      <Tooltip title={toUtcIso(user.createdAt)}>
                        <Typography variant="body2">
                          {formatDateTime(user.createdAt)}
                        </Typography>
                      </Tooltip>
                    ) : (
                      <Typography variant="body2">—</Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    <Tooltip title="Kontoradering är tillfälligt spärrad">
                      <span>
                        <IconButton
                          size="small"
                          color="error"
                          disabled
                          aria-label={`Radering spärrad för ${user.userId}`}
                          aria-describedby="account-deletion-unavailable"
                        >
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </span>
                    </Tooltip>
                  </TableCell>
                </TableRow>
              ))}
              {users.length === 0 && (
                <TableRow>
                  <TableCell colSpan={10} align="center">
                    <Typography variant="body2" color="text.secondary">Inga användare</Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      <Snackbar
        open={snackbar.open}
        autoHideDuration={4000}
        onClose={() => setSnackbar((s: typeof snackbar) => ({ ...s, open: false }))}
      >
        <Alert severity={snackbar.severity} onClose={() => setSnackbar((s: typeof snackbar) => ({ ...s, open: false }))}>
          {snackbar.message}
        </Alert>
      </Snackbar>
    </Container>
  );
}
