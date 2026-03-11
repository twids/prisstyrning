import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function usePriceTrend(enabled = true) {
  return useQuery({
    queryKey: ['prices', 'trend'],
    queryFn: () => apiClient.getPriceTrend(),
    enabled,
    staleTime: 5 * 60 * 1000,
    refetchInterval: 15 * 60 * 1000, // 15 minutes, less frequent
  });
}
