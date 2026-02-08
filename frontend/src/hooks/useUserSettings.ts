import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import type { UserSettings } from '../types/api';

export function useUserSettings() {
  const queryClient = useQueryClient();

  const query = useQuery({
    queryKey: ['user', 'settings'],
    queryFn: () => apiClient.getUserSettings(),
    staleTime: 60 * 1000, // 1 minute
  });

  const updateMutation = useMutation({
    mutationFn: (settings: UserSettings) => apiClient.saveUserSettings(settings),
    onSuccess: () => {
      // Invalidate settings and schedule preview
      queryClient.invalidateQueries({ queryKey: ['user', 'settings'] });
      queryClient.invalidateQueries({ queryKey: ['schedule', 'preview'] });
    },
  });

  return {
    settings: query.data,
    isLoading: query.isLoading,
    error: query.error,
    updateSettings: updateMutation.mutateAsync,
    isUpdating: updateMutation.isPending,
  };
}
