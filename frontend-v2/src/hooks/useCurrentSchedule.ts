import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useCurrentSchedule() {
  return useMutation({
    mutationFn: (embeddedId?: string) => apiClient.getCurrentSchedule(embeddedId),
  });
}
