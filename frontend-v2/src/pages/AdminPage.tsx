import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import Card from '../components/Card';
import ConfirmDialog from '../components/ConfirmDialog';
import { apiClient } from '../api/client';
import { useToast } from '../context/ToastContext';
import type { AdminUser } from '../types/api';

export default function AdminPage() {
  const queryClient = useQueryClient();
  const { showToast } = useToast();
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
      showToast(`Admin toggle failed: ${err.message}`, 'error');
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
      showToast(`Hangfire toggle failed: ${err.message}`, 'error');
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (userId: string) => apiClient.deleteUser(userId),
    onSuccess: () => {
      setDeleteTarget(null);
      showToast('Användare borttagen', 'success');
      queryClient.invalidateQueries({ queryKey: ['admin-users'] });
    },
    onError: (err: Error) => {
      showToast(`Kunde inte ta bort: ${err.message}`, 'error');
    },
  });

  const handleLogin = (e: React.FormEvent) => {
    e.preventDefault();
    if (!password.trim()) return;
    loginMutation.mutate(password);
  };

  // Loading state
  if (statusQuery.isLoading) {
    return (
      <div className="flex justify-center py-12">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-gray-300 border-t-blue-600" />
      </div>
    );
  }

  // Login form
  if (!isAdmin) {
    return (
      <div className="max-w-sm mx-auto">
        <Card>
          <h1 className="text-2xl font-bold mb-4">Admin</h1>
          <form onSubmit={handleLogin} className="space-y-3">
            <div>
              <label className="block text-sm font-medium mb-1">Lösenord</label>
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoFocus
                className="w-full px-3 py-2 rounded-lg border border-gray-300 dark:border-gray-700 bg-white dark:bg-gray-900 text-sm focus:ring-2 focus:ring-blue-500 focus:border-blue-500 outline-none transition-colors"
              />
            </div>
            {loginError && (
              <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-3 text-sm text-red-600 dark:text-red-400">
                {loginError}
              </div>
            )}
            <button
              type="submit"
              disabled={loginMutation.isPending || !password.trim()}
              className="w-full px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50"
            >
              {loginMutation.isPending ? 'Loggar in...' : 'Logga in'}
            </button>
          </form>
        </Card>
      </div>
    );
  }

  // Admin: User table
  const users = usersQuery.data?.users ?? [];

  return (
    <div className="space-y-6">
      <h1 className="text-2xl md:text-3xl font-bold">Användare</h1>

      {usersQuery.isLoading && (
        <div className="flex justify-center py-8">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-gray-300 border-t-blue-600" />
        </div>
      )}

      {usersQuery.error && (
        <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-3 text-sm text-red-600 dark:text-red-400">
          Kunde inte hämta användare: {(usersQuery.error as Error).message}
        </div>
      )}

      {usersQuery.data && (
        <Card className="overflow-x-auto !p-0">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-900/50">
                <th className="text-left px-4 py-3 font-medium">Användare</th>
                <th className="text-left px-4 py-3 font-medium">Zon</th>
                <th className="text-left px-4 py-3 font-medium">Inställningar</th>
                <th className="text-left px-4 py-3 font-medium">Daikin</th>
                <th className="text-left px-4 py-3 font-medium">Daikin Subject</th>
                <th className="text-left px-4 py-3 font-medium">Schema</th>
                <th className="text-left px-4 py-3 font-medium">Admin</th>
                <th className="text-left px-4 py-3 font-medium">Hangfire</th>
                <th className="text-left px-4 py-3 font-medium">Skapad</th>
                <th className="text-left px-4 py-3 font-medium">Åtgärd</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => (
                <tr
                  key={user.userId}
                  className={`border-b border-gray-200 dark:border-gray-800 last:border-0 ${user.isCurrentUser ? 'bg-blue-50 dark:bg-blue-950/30' : ''}`}
                >
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <span className="font-mono text-xs select-all" title={user.userId}>
                        {user.userId}
                      </span>
                      {user.isCurrentUser && (
                        <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 dark:bg-blue-900 text-blue-800 dark:text-blue-200">
                          Du
                        </span>
                      )}
                    </div>
                  </td>
                  <td className="px-4 py-3">{user.zone || '—'}</td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {user.settings.ComfortHours}h, {(user.settings.TurnOffPercentile * 100).toFixed(0)}%
                  </td>
                  <td className="px-4 py-3">
                    {user.daikinAuthorized ? (
                      <span
                        className="text-green-600 dark:text-green-400"
                        title={user.daikinExpiresAtUtc ? `Utgår: ${user.daikinExpiresAtUtc}` : 'Auktoriserad'}
                      >
                        &#10003;
                      </span>
                    ) : (
                      <span className="text-red-500 dark:text-red-400" title="Ej auktoriserad">
                        &#10007;
                      </span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    {user.daikinSubject ? (
                      <span
                        className="font-mono text-xs select-all max-w-[120px] truncate inline-block"
                        title={user.daikinSubject}
                      >
                        {user.daikinSubject}
                      </span>
                    ) : (
                      <span className="text-gray-400">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    {user.hasScheduleHistory ? (
                      <span title={user.lastScheduleDate ? `Senast: ${user.lastScheduleDate}` : ''}>
                        {user.scheduleCount} st
                      </span>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="px-4 py-3">
                    {pendingToggles.has(`admin-${user.userId}`) ? (
                      <div className="h-5 w-5 animate-spin rounded-full border-2 border-gray-300 border-t-blue-600" />
                    ) : (
                      <label className="relative inline-flex items-center cursor-pointer">
                        <input
                          type="checkbox"
                          checked={user.isAdmin}
                          disabled={user.isCurrentUser}
                          onChange={() => toggleAdminMutation.mutate(user)}
                          className="sr-only peer"
                        />
                        <div className="w-9 h-5 bg-gray-200 dark:bg-gray-700 peer-focus:ring-2 peer-focus:ring-blue-500 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:bg-blue-600 peer-disabled:opacity-50 peer-disabled:cursor-not-allowed"></div>
                      </label>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    {pendingToggles.has(`hangfire-${user.userId}`) ? (
                      <div className="h-5 w-5 animate-spin rounded-full border-2 border-gray-300 border-t-blue-600" />
                    ) : (
                      <label className="relative inline-flex items-center cursor-pointer">
                        <input
                          type="checkbox"
                          checked={user.hasHangfireAccess}
                          onChange={() => toggleHangfireMutation.mutate(user)}
                          className="sr-only peer"
                        />
                        <div className="w-9 h-5 bg-gray-200 dark:bg-gray-700 peer-focus:ring-2 peer-focus:ring-blue-500 rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:bg-blue-600"></div>
                      </label>
                    )}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    {user.createdAt ? (
                      <span title={new Date(user.createdAt).toISOString()}>
                        {new Date(user.createdAt).toLocaleString(undefined, {
                          year: 'numeric',
                          month: '2-digit',
                          day: '2-digit',
                          hour: '2-digit',
                          minute: '2-digit',
                        })}
                      </span>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <button
                      onClick={() => setDeleteTarget(user)}
                      disabled={user.isCurrentUser}
                      title={user.isCurrentUser ? 'Kan inte ta bort dig själv' : 'Ta bort användare'}
                      aria-label={user.isCurrentUser ? 'Kan inte ta bort din egen användare' : `Ta bort användare ${user.userId.slice(0, 8)}`}
                      className="p-1 text-red-500 hover:text-red-700 dark:hover:text-red-300 transition-colors disabled:opacity-30 disabled:cursor-not-allowed"
                    >
                      <svg xmlns="http://www.w3.org/2000/svg" className="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
                        <path fillRule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clipRule="evenodd" />
                      </svg>
                    </button>
                  </td>
                </tr>
              ))}
              {users.length === 0 && (
                <tr>
                  <td colSpan={10} className="px-4 py-8 text-center text-gray-500 dark:text-gray-400">
                    Inga användare
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </Card>
      )}

      {/* Delete User Confirm Dialog */}
      <ConfirmDialog
        open={deleteTarget !== null}
        title="Ta bort användare"
        message={`Är du säker på att du vill ta bort användare ${deleteTarget?.userId?.slice(0, 8)}…? All data (inställningar, tokens, schemahistorik) kommer att raderas permanent.`}
        confirmText={deleteMutation.isPending ? 'Tar bort...' : 'Ta bort'}
        cancelText="Avbryt"
        onConfirm={() => deleteTarget && deleteMutation.mutate(deleteTarget.userId)}
        onCancel={() => setDeleteTarget(null)}
        isDestructive
      />
    </div>
  );
}
