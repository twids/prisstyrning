import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';

export function useScheduleEntries() {
  const queryClient = useQueryClient();

  const entries = useQuery({
    queryKey: ['schedule', 'entries'],
    queryFn: () => apiClient.getScheduleEntries(),
    refetchInterval: 60000,
  });

  const addEntry = useMutation({
    mutationFn: (entry: { scheduledTime: string; state: string; countsAsLegionella: boolean }) =>
      apiClient.addScheduleEntry(entry),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['schedule'] });
      queryClient.invalidateQueries({ queryKey: ['user', 'flexible-state'] });
    },
  });

  const removeEntry = useMutation({
    mutationFn: (id: number) => apiClient.removeScheduleEntry(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['schedule'] });
      queryClient.invalidateQueries({ queryKey: ['user', 'flexible-state'] });
    },
  });

  return { entries, addEntry, removeEntry };
}
