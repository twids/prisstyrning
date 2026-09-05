// API Type Definitions for Prisstyrning

export interface DaikinAuthStatus {
  authorized: boolean;
  expiresAtUtc?: string;
}

export interface SessionStatus {
  authenticated: boolean;
  userId: string | null;
  isAdmin: boolean;
  csrfToken: string | null;
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
  Timezone: string;
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
  CurrentThreshold: number | null;
  BaseThreshold: number | null;
  TrendFactor: number;
  Currency: string;
}

export interface PriceThresholdResponse {
  percentile: number;
  threshold: number | null;
  maxPrice: number | null;
  trendFactor: number;
  currency: string;
  lookbackDays: number;
  zone: string;
}

export interface DailyAverage {
  date: string;
  avgPrice: number;
}

export interface PriceTrendResponse {
  zone: string;
  trendFactor: number;
  lookbackDays: number;
  dailyAverages: DailyAverage[];
}

// Intelligent thermal orchestration
export type ControlMode = 'Legacy' | 'Shadow' | 'LwtActive' | 'FullActive';
export type DhwWriter = 'Legacy' | 'Joint';
export type DataQuality = 'Valid' | 'Stale' | 'Invalid' | 'Unavailable';

export interface ThermalSiteConfig {
  userId: string;
  controlMode: ControlMode;
  dhwWriter: DhwWriter;
  baseRoomTargetC: number;
  lowerComfortBandC: number;
  upperComfortBandC: number;
  activeDeviationLimitC: number;
  tariffEnabled: boolean;
  heatPumpPowerSignVerified: boolean;
  weatherCurveVerified: boolean;
  comfortSetpointConfirmed: boolean;
  comfortSetpointC: number;
  comfortIntervalDays: number;
  comfortFlexibilityDays: number;
  timeZone: string;
  variableCostComponentsJson: string;
  tariffDefinitionJson: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface ThermalRoomConfig {
  id: number;
  userId: string;
  name: string;
  entityId: string;
  targetOffsetC: number;
  weight: number;
  isCritical: boolean;
  enabled: boolean;
  minimumValidC: number;
  maximumValidC: number;
  maximumRateCPerHour: number;
}

export interface ThermalEntityConfig {
  id: number;
  userId: string;
  role: string;
  entityId: string;
  expectedUnit: string;
  enabled: boolean;
  minimumValid: number | null;
  maximumValid: number | null;
  maximumRatePerHour: number | null;
}

export interface ThermalConfig {
  site: ThermalSiteConfig;
  rooms: ThermalRoomConfig[];
  entities: ThermalEntityConfig[];
}

export interface ThermalStatus {
  mode: ControlMode;
  dhwWriter: DhwWriter;
  lastTelemetryUtc: string | null;
  overallDataQuality: DataQuality;
  dataQualityReason?: string | null;
  emhassAvailable: boolean;
  planCreatedUtc: string | null;
  planAgeMinutes: number | null;
  currentLwtDeviationC: number;
  fallbackReason: string | null;
  nextControlEventUtc: string | null;
  manualOverride: boolean;
}

export interface ReadinessCheck {
  key: string;
  requirement: string;
  passed: boolean;
  action: string;
  severity: 'Information' | 'Warning' | 'ActionRequired';
}

export interface ThermalReadiness {
  targetMode: ControlMode;
  ready: boolean;
  checks: ReadinessCheck[];
}

export interface DecisionReason {
  mainReason: string;
  price: number | null;
  comfortMarginC: number | null;
  modelConfidence: number;
  alternative: string | null;
}

export interface ThermalPlanStep {
  id: number;
  thermalPlanId: string;
  startUtc: string;
  endUtc: string;
  desiredHeatOutputKw: number;
  desiredLwtDeviationC: number;
  dhwReserved: boolean;
  dhwMode: string;
  incrementalCost: number;
  confidence: number;
  expectedRoomsJson: string;
  decisionReasonJson: string;
}

export interface ThermalPlan {
  id: string;
  userId: string;
  createdAtUtc: string;
  validFromUtc: string;
  validUntilUtc: string;
  status: string;
  isShadow: boolean;
  solverDurationMs: number;
  objectiveCost: number | null;
  confidence: number;
  summary: string;
  inputSnapshotJson: string;
  steps: ThermalPlanStep[];
}

export interface ThermalTelemetrySample {
  id: number;
  userId: string;
  timestampUtc: string;
  outsideTemperatureC: number | null;
  outsideTemperatureForecastJson: string;
  windSpeedMps: number | null;
  solarIrradianceWm2: number | null;
  leavingWaterTemperatureC: number | null;
  returnWaterTemperatureC: number | null;
  flowLitresPerMinute: number | null;
  brineInC: number | null;
  brineOutC: number | null;
  tankTemperatureC: number | null;
  heatPumpPowerKw: number | null;
  propertyPowerKw: number | null;
  spotPriceSekPerKwh: number | null;
  heatOutputKw: number | null;
  cop: number | null;
  dhwActive: boolean | null;
  defrostActive: boolean | null;
  backupHeaterActive: boolean | null;
  roomTemperaturesJson: string;
  qualityJson: string;
}

export interface ThermalEvent {
  id: number;
  userId: string;
  timestampUtc: string;
  severity: 'Information' | 'Warning' | 'ActionRequired';
  category: string;
  message: string;
  detailsJson: string;
}

export interface DhwCycle {
  id: number;
  kind: 'Eco' | 'Comfort';
  source: 'Legacy' | 'LegacyObserved' | 'Shadow' | 'Joint' | 'JointObserved';
  status: string;
  plannedStartUtc: string;
  scheduleAcceptedUtc: string | null;
  actualStartUtc: string | null;
  targetReachedUtc: string | null;
  actualEndUtc: string | null;
  startTemperatureC: number | null;
  targetTemperatureC: number;
  predictedDurationMinutes: number;
  reservedDurationMinutes: number;
  predictedCost: number | null;
  actualCost: number | null;
  backupHeaterUsed: boolean;
  targetVerificationCount: number;
  estimatedCompletionUtc: string | null;
}

export interface ThermalModelVersion {
  id: number;
  modelType: string;
  createdAtUtc: string;
  trainingFromUtc: string;
  trainingToUtc: string;
  isActive: boolean;
  parametersJson: string;
  metricsJson: string;
  provenance?: ThermalModelProvenance;
  sourceValidation?: ThermalModelSourceValidation;
  validation?: ThermalModelValidation;
}

export interface ThermalModelProvenance {
  verifiable: boolean;
  algorithmVersion: string | null;
  selectionVersion: string | null;
  buildRevision: string | null;
  selectionFromUtc: string | null;
  selectionToUtc: string | null;
  observationCount: number | null;
  trainingSamples: number | null;
  validationSamples: number | null;
}

export interface ThermalModelValidation {
  passed: boolean;
  status: 'Missing' | 'Invalid' | 'Unproven' | 'Insufficient' | 'Validated' | 'ThresholdExceeded' | 'SourceChanged' | 'BuildChanged';
  reason: string;
  checkedAtUtc: string;
  twoHourMaeC: number | null;
  dayMaeC: number | null;
  copMae: number | null;
  twoHourValidationWindows: number | null;
  dayValidationWindows: number | null;
}

export interface ThermalModelSourceValidation {
  passed: boolean;
  status: 'Current' | 'Changed' | 'BuildChanged' | 'Invalid' | 'Unproven';
  reason: string;
  checkedAtUtc: string;
}

export interface HomeAssistantStatus {
  configured: boolean;
  connected: boolean;
  lastSnapshotUtc: string | null;
  lastActivityUtc: string | null;
  cachedEntities: number;
  // Additive; older responses are displayed as unverified, never assumed live.
  phase?: 'NotConfigured' | 'Disabled' | 'Reloading' | 'Connecting' | 'Synchronizing' | 'Connected' | 'Reconnecting' | 'Disconnected';
  configurationUpdatedAtUtc?: string | null;
}

export interface HomeAssistantConnection {
  baseUrl: string;
  telemetryEnabled: boolean;
  controlEnabled: boolean;
  heatingDeviationEntityId: string;
  staleAfterMinutes: number;
  telemetryTokenConfigured: boolean;
  controlTokenConfigured: boolean;
  updatedAtUtc: string;
}

export interface UpdateHomeAssistantConnection {
  baseUrl: string;
  telemetryToken: string | null;
  controlToken: string | null;
  telemetryEnabled: boolean;
  controlEnabled: boolean;
  heatingDeviationEntityId: string;
  staleAfterMinutes: number;
  clearControlToken: boolean;
}

export interface HomeAssistantEntity {
  entityId: string;
  friendlyName: string;
  state: string;
  unit: string | null;
  lastUpdatedUtc: string | null;
  lastReportedUtc?: string | null;
  receivedAtUtc: string;
  quality: DataQuality;
  qualityReason: string | null;
  /** Preliminary value/unit checks only, not sensor-health or readiness approval. */
  compatibleUnits?: string[];
  checkedAtUtc?: string | null;
  validUntilUtc?: string | null;
}

export interface HomeAssistantHistoryImportResult {
  importedSamples: number;
  existingSamplesPreserved: number;
  requestedEntities: number;
  entitiesWithoutHistory: string[];
}
