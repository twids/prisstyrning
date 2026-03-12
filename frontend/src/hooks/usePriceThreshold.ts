import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function usePriceThreshold(percentile: number, enabled = true) {
  return useQuery({
    queryKey: ['prices', 'threshold', percentile],
    queryFn: () => apiClient.getPriceThreshold(percentile),
    enabled,
    staleTime: 5 * 60 * 1000, // 5 minutes (aligns with backend 1h cache)
    refetchInterval: 5 * 60 * 1000,
  });
}
