import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useGatewayDevices(enabled: boolean = false) {
  return useQuery({
    queryKey: ['daikin', 'gateway'],
    queryFn: () => apiClient.getGatewayDevices(),
    enabled, // Only fetch when explicitly enabled
    staleTime: 60 * 1000, // 1 minute
  });
}
