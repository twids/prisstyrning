import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useSchedulePreview() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => apiClient.getSchedulePreview(),
    onSuccess: () => {
      // Invalidate history to show newly persisted schedule
      queryClient.invalidateQueries({ queryKey: ['schedule', 'history'] });
    },
  });
}
