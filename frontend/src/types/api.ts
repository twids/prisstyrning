// API Type Definitions for Prisstyrning

export interface DaikinAuthStatus {
  authorized: boolean;
  expiresAtUtc?: string;
}

export interface PricePoint {
  start: string;
  value: number;
  day: 'today' | 'tomorrow';
}

export interface PriceTimeseries {
  updated?: string;
  count: number;
  items: PricePoint[];
  source: 'memory' | 'latest';
}

export type ScheduleState = 'comfort' | 'turn_off'; // Updated for 2-mode system post-ECO removal

export interface SchedulePayload {
  [scheduleId: string]: {
    actions: {
      [day: string]: {
        [time: string]: {
          domesticHotWaterTemperature?: ScheduleState;
          roomTemperature?: ScheduleState;
        };
      };
    };
  };
}

export interface UserSettings {
  ComfortHours: number;
  TurnOffPercentile: number;
  AutoApplySchedule: boolean;
  MaxComfortGapHours: number;
  // Note: TurnOffMaxConsecutive removed in Phase 4
}

export interface SchedulePreviewResponse {
  schedulePayload: SchedulePayload | null;
  generated: boolean;
  message?: string;
  zone?: string;
}

export interface ScheduleHistoryEntry {
  timestamp: string;
  date: string;
  schedule: SchedulePayload;
}

export interface ZoneResponse {
  zone: string;
}

export interface SaveZoneRequest {
  zone: string;
}

export interface SaveZoneResponse {
  saved: boolean;
  zone: string;
}

export interface SaveSettingsResponse {
  saved: boolean;
}

export interface AuthUrlResponse {
  url: string;
}

export interface AuthRefreshResponse {
  refreshed: boolean;
}

export interface AuthRevokeResponse {
  revoked: boolean;
}

export interface ApplyScheduleRequest {
  gatewayDeviceId?: string; // Optional - will be auto-detected if not provided
  embeddedId?: string; // Optional - will be auto-detected if not provided
  mode?: string;
  schedulePayload: SchedulePayload;
  activateScheduleId?: string;
}

export interface ApplyScheduleResponse {
  put: boolean;
  activateScheduleId?: string;
  modeUsed: string;
  requestedMode: string;
}

export interface StatusResponse {
  status: string;
  timestamp: string;
}
