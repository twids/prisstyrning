import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useAuth() {
  const queryClient = useQueryClient();

  // Query auth status every minute
  const { data, isLoading, error } = useQuery({
    queryKey: ['auth', 'status'],
    queryFn: () => apiClient.getAuthStatus(),
    refetchInterval: 60 * 1000, // 1 minute
    retry: 1,
  });

  // Mutation for manual refresh
  const refreshMutation = useMutation({
    mutationFn: () => apiClient.refreshAuth(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['auth'] });
    },
  });

  // Mutation for token revocation
  const revokeMutation = useMutation({
    mutationFn: () => apiClient.revokeAuth(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['auth'] });
    },
  });

  // Function to start OAuth flow
  const startAuth = async () => {
    const { url } = await apiClient.startAuth();
    window.location.href = url; // Redirect to Daikin OAuth
  };

  return {
    isAuthorized: data?.authorized ?? false,
    expiresAt: data?.expiresAtUtc ?? null,
    isLoading,
    error,
    startAuth,
    refresh: refreshMutation.mutateAsync,
    revoke: revokeMutation.mutateAsync,
    isRefreshing: refreshMutation.isPending,
    isRevoking: revokeMutation.isPending,
  };
}
