import { useState } from 'react';
import {
  Container,
  Paper,
  Typography,
  TextField,
  Button,
  Alert,
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
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogActions from '@mui/material/DialogActions';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import type { AdminUser } from '../types/api';

export default function AdminPage() {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState<string | null>(null);
  const [snackbar, setSnackbar] = useState<{ open: boolean; message: string; severity: 'error' | 'success' }>({ open: false, message: '', severity: 'error' });
  const [pendingToggles, setPendingToggles] = useState<Set<string>>(new Set());
  const [deleteTarget, setDeleteTarget] = useState<AdminUser | null>(null);

  const statusQuery = useQuery({
    queryKey: ['admin-status'],
    queryFn: () => apiClient.getAdminStatus(),
  });

  const isAdmin = statusQuery.data?.isAdmin ?? false;

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
    onError: (err: Error) => {
      setLoginError(err.message || 'Login failed');
    },
  });

  const toggleAdminMutation = useMutation<void, Error, AdminUser>({
    mutationFn: (user) =>
      user.isAdmin ? apiClient.revokeAdmin(user.userId) : apiClient.grantAdmin(user.userId),
    onMutate: (user) => {
      setPendingToggles((prev: Set<string>) => new Set(prev).add(`admin-${user.userId}`));
    },
    onSettled: (_data: void | undefined, _err: Error | null, user: AdminUser | undefined) => {
      if (user) {
        setPendingToggles((prev: Set<string>) => {
          const next = new Set(prev);
          next.delete(`admin-${user.userId}`);
          return next;
        });
      }
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err) => {
      setSnackbar({ open: true, message: `Admin toggle failed: ${err.message}`, severity: 'error' });
    },
  });

  const toggleHangfireMutation = useMutation<void, Error, AdminUser>({
    mutationFn: (user) =>
      user.hasHangfireAccess ? apiClient.revokeHangfire(user.userId) : apiClient.grantHangfire(user.userId),
    onMutate: (user) => {
      setPendingToggles((prev: Set<string>) => new Set(prev).add(`hangfire-${user.userId}`));
    },
    onSettled: (_data: void | undefined, _err: Error | null, user: AdminUser | undefined) => {
      if (user) {
        setPendingToggles((prev: Set<string>) => {
          const next = new Set(prev);
          next.delete(`hangfire-${user.userId}`);
          return next;
        });
      }
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err) => {
      setSnackbar({ open: true, message: `Hangfire toggle failed: ${err.message}`, severity: 'error' });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (userId: string) => apiClient.deleteUser(userId),
    onSuccess: () => {
      setDeleteTarget(null);
      setSnackbar({ open: true, message: 'Användare borttagen', severity: 'success' });
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err: Error) => {
      setSnackbar({ open: true, message: `Kunde inte ta bort: ${err.message}`, severity: 'error' });
    },
  });

  const handleLogin = (e: React.FormEvent) => {
    e.preventDefault();
    if (!password.trim()) return;
    loginMutation.mutate(password);
  };

  if (statusQuery.isLoading) {
    return (
      <Container maxWidth="lg" sx={{ py: 4, display: 'flex', justifyContent: 'center' }}>
        <CircularProgress />
      </Container>
    );
  }

  // Login form
  if (!isAdmin) {
    return (
      <Container maxWidth="sm" sx={{ py: 4 }}>
        <Paper sx={{ p: 4 }}>
          <Typography variant="h4" gutterBottom>
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
      <Typography variant="h4" gutterBottom sx={{ fontSize: { xs: '1.5rem', md: '2rem' } }}>
        Användare
      </Typography>

      {usersQuery.isLoading && (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}>
          <CircularProgress />
        </Box>
      )}

      {usersQuery.error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          Kunde inte hämta användare: {(usersQuery.error as Error).message}
        </Alert>
      )}

      {usersQuery.data && (
        <TableContainer component={Paper}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Användare</TableCell>
                <TableCell>Zon</TableCell>
                <TableCell>Inställningar</TableCell>
                <TableCell>Daikin</TableCell>
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
                        <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
                          {user.userId.length > 8 ? `${user.userId.slice(0, 8)}…` : user.userId}
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
                      <Tooltip title={user.daikinExpiresAtUtc ? `Utgår: ${user.daikinExpiresAtUtc}` : 'Auktoriserad'}>
                        <CheckCircleIcon color="success" fontSize="small" />
                      </Tooltip>
                    ) : (
                      <Tooltip title="Ej auktoriserad">
                        <CancelIcon color="error" fontSize="small" />
                      </Tooltip>
                    )}
                  </TableCell>
                  <TableCell>
                    {user.hasScheduleHistory ? (
                      <Tooltip title={user.lastScheduleDate ? `Senast: ${user.lastScheduleDate}` : ''}>
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
                        disabled={user.isCurrentUser}
                        onChange={() => toggleAdminMutation.mutate(user)}
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
                        onChange={() => toggleHangfireMutation.mutate(user)}
                        size="small"
                      />
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">
                      {user.createdAt ? user.createdAt.slice(0, 10) : '—'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Tooltip title={user.isCurrentUser ? 'Kan inte ta bort dig själv' : 'Ta bort användare'}>
                      <span>
                        <IconButton
                          size="small"
                          color="error"
                          disabled={user.isCurrentUser}
                          onClick={() => setDeleteTarget(user)}
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
                  <TableCell colSpan={9} align="center">
                    <Typography variant="body2" color="text.secondary">Inga användare</Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      <Dialog open={deleteTarget !== null} onClose={() => setDeleteTarget(null)}>
        <DialogTitle>Ta bort användare</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Är du säker på att du vill ta bort användare{' '}
            <strong>{deleteTarget?.userId.slice(0, 8)}…</strong>?
            {' '}All data (inställningar, tokens, schemahistorik) kommer att raderas permanent.
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDeleteTarget(null)}>Avbryt</Button>
          <Button
            onClick={() => deleteTarget && deleteMutation.mutate(deleteTarget.userId)}
            color="error"
            variant="contained"
            disabled={deleteMutation.isPending}
            startIcon={deleteMutation.isPending ? <CircularProgress size={18} /> : <DeleteIcon />}
          >
            Ta bort
          </Button>
        </DialogActions>
      </Dialog>

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
