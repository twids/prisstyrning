import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useSession() {
  return useQuery({
    queryKey: ['session'],
    queryFn: () => apiClient.getSession(),
    staleTime: 60_000,
    refetchInterval: 5 * 60_000,
    retry: 1,
  });
}

export function useLogout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => apiClient.logout(),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['session'] });
      queryClient.clear();
      window.location.assign('/');
    },
  });
}
