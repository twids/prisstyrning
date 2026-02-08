import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import type { SchedulePayload } from '../types/api';

interface ApplyScheduleParams {
  gatewayDeviceId: string;
  embeddedId: string;
  schedulePayload: SchedulePayload;
  mode?: string;
  activateScheduleId?: string;
}

export function useApplySchedule() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (params: ApplyScheduleParams) =>
      apiClient.applySchedule(params),
    onSuccess: () => {
      // Invalidate current schedule to refetch updated state
      queryClient.invalidateQueries({ queryKey: ['schedule', 'current'] });
    },
  });
}
