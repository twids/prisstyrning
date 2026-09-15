import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api/client';

export function useConservativeStartup() {
  return useQuery({ queryKey: ['thermal', 'startup'], queryFn: () => apiClient.getThermalStartup(), refetchInterval: 30_000 });
}

export function useConservativePreview() {
  return useQuery({ queryKey: ['thermal', 'conservative-preview'], queryFn: () => apiClient.getConservativePreview(), refetchInterval: 60_000 });
}

export function useStartConservativeHeating() {
  const queries = useQueryClient();
  return useMutation({ mutationFn: () => apiClient.startConservativeHeating(),
    onSettled: () => queries.invalidateQueries({ queryKey: ['thermal'] }) });
}
