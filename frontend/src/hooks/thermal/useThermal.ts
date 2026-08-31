import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api/client';
import type { ControlMode, ThermalConfig, UpdateHomeAssistantConnection } from '../../types/api';

export function useThermalStatus() {
  return useQuery({
    queryKey: ['thermal', 'status'],
    queryFn: () => apiClient.getThermalStatus(),
    refetchInterval: 30_000,
  });
}

export function useThermalConfig() {
  return useQuery({
    queryKey: ['thermal', 'config'],
    queryFn: () => apiClient.getThermalConfig(),
  });
}

export function useSaveThermalConfig() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (config: ThermalConfig) => apiClient.saveThermalConfig(config),
    onSuccess: (config) => {
      queryClient.setQueryData(['thermal', 'config'], config);
      void queryClient.invalidateQueries({ queryKey: ['thermal'] });
    },
  });
}

export function useThermalReadiness(targetMode: ControlMode, enabled = true) {
  return useQuery({
    queryKey: ['thermal', 'readiness', targetMode],
    queryFn: () => apiClient.getThermalReadiness(targetMode),
    enabled,
    refetchInterval: enabled ? 30_000 : false,
  });
}

export function useChangeThermalMode() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (mode: ControlMode) => apiClient.changeThermalMode(mode),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['thermal'] }),
  });
}

export function useThermalPlan() {
  return useQuery({
    queryKey: ['thermal', 'plan'],
    queryFn: () => apiClient.getThermalPlan(),
    refetchInterval: 60_000,
  });
}

export function useThermalHistory(hours = 48) {
  const to = new Date();
  const from = new Date(to.getTime() - hours * 60 * 60 * 1000);
  return useQuery({
    queryKey: ['thermal', 'history', hours, Math.floor(to.getTime() / 300_000)],
    queryFn: () => apiClient.getThermalHistory(from.toISOString(), to.toISOString()),
    refetchInterval: 5 * 60_000,
  });
}

export function useThermalEvents(limit = 100) {
  return useQuery({
    queryKey: ['thermal', 'events', limit],
    queryFn: () => apiClient.getThermalEvents(limit),
    refetchInterval: 60_000,
  });
}

export function useDhwCycles() {
  return useQuery({
    queryKey: ['thermal', 'dhw'],
    queryFn: () => apiClient.getDhwCycles(),
    refetchInterval: 60_000,
  });
}

export function useThermalModels() {
  return useQuery({
    queryKey: ['thermal', 'models'],
    queryFn: () => apiClient.getThermalModels(),
    refetchInterval: 60_000,
  });
}

export function useHomeAssistant() {
  const queryClient = useQueryClient();
  const config = useQuery({
    queryKey: ['home-assistant', 'config'],
    queryFn: () => apiClient.getHomeAssistantConfig(),
  });
  const status = useQuery({
    queryKey: ['home-assistant', 'status'],
    queryFn: () => apiClient.getHomeAssistantStatus(),
    refetchInterval: 30_000,
  });
  const entities = useQuery({
    queryKey: ['home-assistant', 'entities'],
    queryFn: () => apiClient.getHomeAssistantEntities(),
    enabled: status.data?.configured === true && status.data.connected &&
      status.data.configurationUpdatedAtUtc === config.data?.updatedAtUtc && !status.isError && !config.isError,
    refetchInterval: 60_000,
  });
  const test = useMutation({
    mutationFn: () => apiClient.testHomeAssistant(),
    onSettled: () => void queryClient.invalidateQueries({ queryKey: ['home-assistant'] }),
  });
  const save = useMutation({
    mutationFn: (request: UpdateHomeAssistantConnection) => apiClient.saveHomeAssistantConfig(request),
    onSuccess: async (connection) => {
      await queryClient.cancelQueries({ queryKey: ['home-assistant'] });
      test.reset();
      queryClient.setQueryData(['home-assistant', 'config'], connection);
      queryClient.setQueryData(['home-assistant', 'status'], {
        configured: connection.telemetryEnabled && connection.telemetryTokenConfigured,
        connected: false, phase: connection.telemetryEnabled ? 'Reloading' : 'Disabled',
        configurationUpdatedAtUtc: connection.updatedAtUtc, lastSnapshotUtc: null, lastActivityUtc: null, cachedEntities: 0,
      });
      queryClient.setQueryData(['home-assistant', 'entities'], []);
      void queryClient.invalidateQueries({ queryKey: ['home-assistant'] });
      void queryClient.invalidateQueries({ queryKey: ['thermal', 'readiness'] });
    },
  });
  const remove = useMutation({
    mutationFn: () => apiClient.deleteHomeAssistantConfig(),
    onSuccess: async () => {
      await queryClient.cancelQueries({ queryKey: ['home-assistant'] });
      test.reset();
      queryClient.setQueryData(['home-assistant', 'config'], null);
      queryClient.setQueryData(['home-assistant', 'status'], {
        configured: false, connected: false, phase: 'NotConfigured', configurationUpdatedAtUtc: null,
        lastSnapshotUtc: null, lastActivityUtc: null, cachedEntities: 0,
      });
      queryClient.setQueryData(['home-assistant', 'entities'], []);
      void queryClient.invalidateQueries({ queryKey: ['home-assistant'] });
      void queryClient.invalidateQueries({ queryKey: ['thermal', 'readiness'] });
    },
  });
  const importHistory = useMutation({
    mutationFn: ({ fromUtc, toUtc }: { fromUtc: string; toUtc: string }) =>
      apiClient.importHomeAssistantHistory(fromUtc, toUtc),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['thermal', 'history'] });
      void queryClient.invalidateQueries({ queryKey: ['thermal', 'events'] });
    },
  });
  return { config, status, entities, test, save, remove, importHistory };
}
