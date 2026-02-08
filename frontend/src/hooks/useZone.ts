import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useZone() {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: ['user', 'zone'],
    queryFn: async () => {
      const response = await apiClient.getZone();
      return response.zone;
    },
    staleTime: 5 * 60 * 1000, // 5 minutes
  });

  const updateMutation = useMutation({
    mutationFn: (zone: string) => apiClient.setZone(zone),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['user', 'zone'] });
      queryClient.invalidateQueries({ queryKey: ['prices', 'timeseries'] });
      queryClient.invalidateQueries({ queryKey: ['schedule', 'preview'] });
    },
  });

  return {
    zone: query.data,
    isLoading: query.isLoading,
    error: query.error,
    setZone: updateMutation.mutateAsync,
    isUpdating: updateMutation.isPending,
  };
}
