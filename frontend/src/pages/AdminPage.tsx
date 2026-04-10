import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { CheckCircle, XCircle, Trash } from '@phosphor-icons/react';
import { Card } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Switch } from '@/components/ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from '@/components/ui/tooltip';
import { apiClient } from '../api/client';
import { useFormatters } from '../context/TimezoneContext';
import type { AdminUser } from '../types/api';

export default function AdminPage() {
  const queryClient = useQueryClient();
  const { formatDateTime } = useFormatters();
  const [password, setPassword] = useState('');
  const [loginError, setLoginError] = useState<string | null>(null);
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
    onError: (err) => {
      toast.error(`Admin toggle failed: ${err.message}`);
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
    onError: (err) => {
      toast.error(`Hangfire toggle failed: ${err.message}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (userId: string) => apiClient.deleteUser(userId),
    onSuccess: () => {
      setDeleteTarget(null);
      toast.success('Användare borttagen');
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err: Error) => {
      toast.error(`Kunde inte ta bort: ${err.message}`);
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
      <div className="flex justify-center py-16">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-border border-t-primary" />
      </div>
    );
  }

  // Login form
  if (!isAdmin) {
    return (
      <div className="flex justify-center mt-12 px-4">
        <Card className="p-6 w-full max-w-sm">
          <h1 className="text-2xl font-bold mb-4">Admin</h1>
          <form onSubmit={handleLogin} className="flex flex-col gap-3">
            <input
              type="password"
              placeholder="Lösenord"
              autoFocus
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full px-3 py-2 rounded-md bg-secondary border border-border text-foreground"
            />
            {loginError && <p className="text-destructive text-sm">{loginError}</p>}
            <Button type="submit" disabled={loginMutation.isPending || !password.trim()}>
              {loginMutation.isPending && (
                <span className="mr-2 h-4 w-4 animate-spin rounded-full border-2 border-background border-t-transparent inline-block" />
              )}
              Logga in
            </Button>
          </form>
        </Card>
      </div>
    );
  }

  // Admin: User table
  const users = usersQuery.data?.users ?? [];

  return (
    <TooltipProvider>
      <div className="px-4 py-6 max-w-screen-xl mx-auto">
        <h1 className="text-2xl font-bold mb-4">Användare</h1>

        {usersQuery.isLoading && (
          <div className="flex justify-center py-8">
            <div className="h-8 w-8 animate-spin rounded-full border-4 border-border border-t-primary" />
          </div>
        )}

        {usersQuery.error && (
          <p className="text-destructive mb-4">
            Kunde inte hämta användare: {(usersQuery.error as Error).message}
          </p>
        )}

        {usersQuery.data && (
          <Card>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Användare</TableHead>
                  <TableHead>Zon</TableHead>
                  <TableHead>Inställningar</TableHead>
                  <TableHead>Daikin</TableHead>
                  <TableHead>Daikin Subject</TableHead>
                  <TableHead>Schema</TableHead>
                  <TableHead>Admin</TableHead>
                  <TableHead>Hangfire</TableHead>
                  <TableHead>Skapad</TableHead>
                  <TableHead>Åtgärd</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {users.map((user) => (
                  <TableRow key={user.userId} className={user.isCurrentUser ? 'bg-secondary/50' : undefined}>
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button type="button" className="font-mono cursor-pointer select-all text-sm bg-transparent border-none p-0 text-inherit">
                              {user.userId.slice(0, 8)}…
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>{user.userId}</TooltipContent>
                        </Tooltip>
                        {user.isCurrentUser && <Badge variant="default">Du</Badge>}
                      </div>
                    </TableCell>
                    <TableCell>{user.zone || '—'}</TableCell>
                    <TableCell className="text-sm whitespace-nowrap">
                      {user.settings.ComfortHours}h, {(user.settings.TurnOffPercentile * 100).toFixed(0)}%
                    </TableCell>
                    <TableCell>
                      {user.daikinAuthorized ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button type="button" className="bg-transparent border-none p-0 cursor-default">
                              <CheckCircle className="text-green-500" size={18} />
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>
                            {user.daikinExpiresAtUtc ? `Utgår: ${formatDateTime(user.daikinExpiresAtUtc)}` : 'Auktoriserad'}
                          </TooltipContent>
                        </Tooltip>
                      ) : (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button type="button" className="bg-transparent border-none p-0 cursor-default">
                              <XCircle className="text-red-500" size={18} />
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>Ej auktoriserad</TooltipContent>
                        </Tooltip>
                      )}
                    </TableCell>
                    <TableCell>
                      {user.daikinSubject ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <button type="button" className="font-mono text-sm cursor-pointer select-all max-w-[120px] overflow-hidden text-ellipsis whitespace-nowrap block bg-transparent border-none p-0 text-inherit text-left">
                              {user.daikinSubject}
                            </button>
                          </TooltipTrigger>
                          <TooltipContent>{user.daikinSubject}</TooltipContent>
                        </Tooltip>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </TableCell>
                    <TableCell>
                      {user.hasScheduleHistory ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <span className="text-sm">{user.scheduleCount} st</span>
                          </TooltipTrigger>
                          <TooltipContent>
                            {user.lastScheduleDate ? `Senast: ${formatDateTime(user.lastScheduleDate)}` : ''}
                          </TooltipContent>
                        </Tooltip>
                      ) : (
                        '—'
                      )}
                    </TableCell>
                    <TableCell>
                      {pendingToggles.has(`admin-${user.userId}`) ? (
                        <span className="h-5 w-5 animate-spin rounded-full border-2 border-border border-t-primary inline-block" />
                      ) : (
                        <Switch
                          checked={user.isAdmin}
                          disabled={user.isCurrentUser}
                          onCheckedChange={() => toggleAdminMutation.mutate(user)}
                        />
                      )}
                    </TableCell>
                    <TableCell>
                      {pendingToggles.has(`hangfire-${user.userId}`) ? (
                        <span className="h-5 w-5 animate-spin rounded-full border-2 border-border border-t-primary inline-block" />
                      ) : (
                        <Switch
                          checked={user.hasHangfireAccess}
                          onCheckedChange={() => toggleHangfireMutation.mutate(user)}
                        />
                      )}
                    </TableCell>
                    <TableCell>
                      {user.createdAt ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <span className="text-sm">{formatDateTime(user.createdAt)}</span>
                          </TooltipTrigger>
                          <TooltipContent>{toUtcIso(user.createdAt)}</TooltipContent>
                        </Tooltip>
                      ) : (
                        <span className="text-sm">—</span>
                      )}
                    </TableCell>
                    <TableCell>
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <span>
                            <Button
                              variant="ghost"
                              size="icon"
                              disabled={user.isCurrentUser}
                              onClick={() => setDeleteTarget(user)}
                              aria-label={user.isCurrentUser ? 'Kan inte ta bort din egen användare' : `Ta bort användare ${user.userId.slice(0, 8)}`}
                              className="text-destructive hover:text-destructive"
                            >
                              <Trash size={16} />
                            </Button>
                          </span>
                        </TooltipTrigger>
                        <TooltipContent>
                          {user.isCurrentUser ? 'Kan inte ta bort dig själv' : 'Ta bort användare'}
                        </TooltipContent>
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                ))}
                {users.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={10} className="text-center text-muted-foreground">
                      Inga användare
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </Card>
        )}

        <Dialog open={deleteTarget !== null} onOpenChange={(open) => { if (!open) setDeleteTarget(null); }}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Ta bort användare</DialogTitle>
              <DialogDescription>
                Är du säker på att du vill ta bort användare{' '}
                <strong>{deleteTarget?.userId?.slice(0, 8)}…</strong>?{' '}
                All data (inställningar, tokens, schemahistorik) kommer att raderas permanent.
              </DialogDescription>
            </DialogHeader>
            <DialogFooter>
              <Button variant="outline" onClick={() => setDeleteTarget(null)}>
                Avbryt
              </Button>
              <Button
                variant="destructive"
                disabled={deleteMutation.isPending}
                onClick={() => deleteTarget && deleteMutation.mutate(deleteTarget.userId)}
              >
                {deleteMutation.isPending ? (
                  <span className="mr-2 h-4 w-4 animate-spin rounded-full border-2 border-background border-t-transparent inline-block" />
                ) : (
                  <Trash className="mr-2" size={16} />
                )}
                Ta bort
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>
    </TooltipProvider>
  );
}
