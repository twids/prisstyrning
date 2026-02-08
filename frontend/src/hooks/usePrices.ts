import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function usePrices(source: 'latest' | 'memory' = 'latest') {
  return useQuery({
    queryKey: ['prices', 'timeseries', source],
    queryFn: () => apiClient.getPriceTimeseries(source),
    refetchInterval: 5 * 60 * 1000, // 5 minutes
    staleTime: 5 * 60 * 1000,
  });
}
