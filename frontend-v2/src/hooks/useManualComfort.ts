import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useManualComfort() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (comfortTime: string) => apiClient.scheduleManualComfort(comfortTime),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['schedule'] });
      queryClient.invalidateQueries({ queryKey: ['user', 'flexible-state'] });
    },
  });
}
