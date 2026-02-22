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

export type ScheduleState = 'comfort' | 'eco' | 'turn_off';

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
  SchedulingMode: 'Classic' | 'Flexible';
  EcoIntervalHours: number;
  EcoFlexibilityHours: number;
  ComfortIntervalDays: number;
  ComfortFlexibilityDays: number;
  ComfortEarlyPercentile: number;
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
  ComfortHours: number;
  TurnOffPercentile: number;
  AutoApplySchedule: boolean;
  MaxComfortGapHours: number;
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

// Admin types
export interface AdminStatus {
  isAdmin: boolean;
  userId: string;
}

export interface AdminUser {
  userId: string;
  settings: {
    ComfortHours: number;
    TurnOffPercentile: number;
    MaxComfortGapHours: number;
  };
  zone: string;
  daikinAuthorized: boolean;
  daikinExpiresAtUtc: string | null;
  daikinSubject: string | null;
  hasScheduleHistory: boolean;
  scheduleCount: number | null;
  lastScheduleDate: string | null;
  isAdmin: boolean;
  isCurrentUser: boolean;
  hasHangfireAccess: boolean;
  createdAt: string | null;
}

export interface AdminUsersResponse {
  users: AdminUser[];
}

export interface FlexibleState {
  LastEcoRunUtc: string | null;
  LastComfortRunUtc: string | null;
  NextScheduledComfortUtc: string | null;
  EcoWindow: {
    Start: string | null;
    End: string | null;
  };
  ComfortWindow: {
    Start: string | null;
    End: string | null;
    Progress: number | null;
  };
  SchedulingMode: string;
}
