import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useScheduleHistory() {
  return useQuery({
    queryKey: ['schedule', 'history'],
    queryFn: () => apiClient.getScheduleHistory(),
    staleTime: 60 * 1000, // 1 minute
  });
}
