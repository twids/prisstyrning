import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import type { FlexibleState } from '../types/api';

export function useFlexibleState(enabled = true) {
  const query = useQuery<FlexibleState>({
    queryKey: ['user', 'flexible-state'],
    queryFn: () => apiClient.getFlexibleState(),
    staleTime: 30 * 1000, // 30 seconds
    enabled,
  });

  return {
    state: query.data,
    isLoading: query.isLoading,
    error: query.error,
    refetch: query.refetch,
  };
}
