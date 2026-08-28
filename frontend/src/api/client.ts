import type * as T from '../types/api';

class ApiClient {
  private baseUrl = ''; // Empty for same-origin requests (Vite proxy handles routing)

  // Auth endpoints
  async getAuthStatus(): Promise<T.DaikinAuthStatus> {
    return this.get('/auth/daikin/status');
  }

  async startAuth(): Promise<T.AuthUrlResponse> {
    return this.get('/auth/daikin/start');
  }

  async refreshAuth(): Promise<T.AuthRefreshResponse> {
    return this.post('/auth/daikin/refresh');
  }

  async revokeAuth(): Promise<T.AuthRevokeResponse> {
    return this.post('/auth/daikin/revoke');
  }

  // Price endpoints
  async getPriceTimeseries(source?: 'latest' | 'memory'): Promise<T.PriceTimeseries> {
    const params = source ? `?source=${source}` : '';
    return this.get(`/api/prices/timeseries${params}`);
  }

  async getPriceThreshold(percentile: number): Promise<T.PriceThresholdResponse> {
    return this.get(`/api/prices/threshold?percentile=${percentile}`);
  }

  async getPriceTrend(): Promise<T.PriceTrendResponse> {
    return this.get('/api/prices/trend');
  }

  async getZone(): Promise<T.ZoneResponse> {
    return this.get('/api/prices/zone');
  }

  async setZone(zone: string): Promise<T.SaveZoneResponse> {
    return this.post('/api/prices/zone', { zone });
  }

  // Schedule endpoints
  async getSchedulePreview(): Promise<T.SchedulePreviewResponse> {
    return this.get('/api/schedule/preview');
  }

  async getScheduleHistory(): Promise<T.ScheduleHistoryEntry[]> {
    return this.get('/api/user/schedule-history');
  }

  // User Settings
  async getUserSettings(): Promise<T.UserSettings> {
    return this.get('/api/user/settings');
  }

  async saveUserSettings(settings: Partial<T.UserSettings>): Promise<T.SaveSettingsResponse> {
    return this.post('/api/user/settings', settings);
  }

  // Daikin endpoints
  async getDaikinSites(): Promise<unknown> {
    return this.get('/api/daikin/sites');
  }

  async getGatewayDevices(): Promise<unknown> {
    return this.get('/api/daikin/gateway?debug=true');
  }

  async getCurrentSchedule(embeddedId?: string): Promise<unknown> {
    const params = embeddedId ? `?embeddedId=${embeddedId}` : '';
    return this.get(`/api/daikin/gateway/schedule${params}`);
  }

  async applySchedule(payload: T.ApplyScheduleRequest): Promise<T.ApplyScheduleResponse> {
    return this.post('/api/daikin/gateway/schedule/put', payload);
  }

  // Flexible scheduling state
  async getFlexibleState(): Promise<T.FlexibleState> {
    return this.get('/api/user/flexible-state');
  }

  // Manual comfort
  async scheduleManualComfort(comfortTime: string): Promise<{ applied: boolean; comfortHour: string; message: string }> {
    return this.post('/api/schedule/comfort', { comfortTime });
  }

  // Status
  async getStatus(): Promise<T.StatusResponse> {
    return this.get('/api/status');
  }

  // Admin endpoints
  async getAdminStatus(): Promise<T.AdminStatus> {
    return this.get('/api/admin/status');
  }

  // Uses custom fetch instead of post() helper since we need X-Admin-Password header without JSON body
  async adminLogin(password: string): Promise<{ granted: boolean; userId: string }> {
    const response = await fetch(this.baseUrl + '/api/admin/login', {
      method: 'POST',
      headers: { 'X-Admin-Password': password },
      credentials: 'same-origin',
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(this.extractErrorMessage(text) || `HTTP ${response.status}`);
    }
    return response.json();
  }

  async getAdminUsers(): Promise<T.AdminUsersResponse> {
    return this.get('/api/admin/users');
  }

  // Intelligent thermal orchestration
  async getThermalStatus(): Promise<T.ThermalStatus> {
    return this.get('/api/thermal/status');
  }

  async getThermalConfig(): Promise<T.ThermalConfig> {
    return this.get('/api/thermal/config');
  }

  async saveThermalConfig(config: T.ThermalConfig): Promise<T.ThermalConfig> {
    return this.put('/api/thermal/config', config);
  }

  async getThermalReadiness(targetMode: T.ControlMode): Promise<T.ThermalReadiness> {
    return this.get(`/api/thermal/readiness?targetMode=${encodeURIComponent(targetMode)}`);
  }

  async changeThermalMode(mode: T.ControlMode): Promise<{ message: string }> {
    return this.post('/api/thermal/mode', { mode, confirmed: true });
  }

  async getThermalPlan(): Promise<T.ThermalPlan | null> {
    const response = await fetch(this.baseUrl + '/api/thermal/plan', { credentials: 'same-origin' });
    if (response.status === 204) return null;
    if (!response.ok) throw new Error(this.extractErrorMessage(await response.text()) || `HTTP ${response.status}`);
    return response.json();
  }

  async getThermalHistory(from: string, to: string): Promise<T.ThermalTelemetrySample[]> {
    return this.get(`/api/thermal/history?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
  }

  async getThermalEvents(limit = 100): Promise<T.ThermalEvent[]> {
    return this.get(`/api/thermal/events?limit=${limit}`);
  }

  async getDhwCycles(): Promise<T.DhwCycle[]> {
    return this.get('/api/thermal/dhw');
  }

  async getThermalModels(): Promise<T.ThermalModelVersion[]> {
    return this.get('/api/thermal/models');
  }

  async getHomeAssistantStatus(): Promise<T.HomeAssistantStatus> {
    return this.get('/api/home-assistant/status');
  }

  async testHomeAssistant(): Promise<{ connected: boolean }> {
    return this.post('/api/home-assistant/test');
  }

  async getHomeAssistantEntities(): Promise<T.HomeAssistantEntity[]> {
    return this.get('/api/home-assistant/entities');
  }

  async importHomeAssistantHistory(fromUtc: string, toUtc: string): Promise<T.HomeAssistantHistoryImportResult> {
    return this.post('/api/home-assistant/import-history', { fromUtc, toUtc });
  }

  async grantAdmin(userId: string): Promise<{ granted: boolean; userId: string }> {
    return this.post(`/api/admin/users/${encodeURIComponent(userId)}/grant`);
  }

  async revokeAdmin(userId: string): Promise<{ revoked: boolean; userId: string }> {
    return this.del(`/api/admin/users/${encodeURIComponent(userId)}/grant`);
  }

  async grantHangfire(userId: string): Promise<{ granted: boolean; userId: string }> {
    return this.post(`/api/admin/users/${encodeURIComponent(userId)}/hangfire`);
  }

  async revokeHangfire(userId: string): Promise<{ revoked: boolean; userId: string }> {
    return this.del(`/api/admin/users/${encodeURIComponent(userId)}/hangfire`);
  }

  async deleteUser(userId: string): Promise<{ deleted: boolean; userId: string }> {
    return this.del(`/api/admin/users/${encodeURIComponent(userId)}`);
  }

  // Helper methods
  private async get<T>(url: string): Promise<T> {
    const response = await fetch(this.baseUrl + url, {
      credentials: 'same-origin', // Include cookies for ps_user
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(this.extractErrorMessage(text) || `HTTP ${response.status}: ${url}`);
    }
    return response.json();
  }

  private async post<T>(url: string, body?: unknown): Promise<T> {
    const response = await fetch(this.baseUrl + url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin', // Include cookies
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(this.extractErrorMessage(text) || `HTTP ${response.status}: ${url}`);
    }
    return response.status === 204 ? undefined as T : response.json();
  }

  private async put<T>(url: string, body: unknown): Promise<T> {
    const response = await fetch(this.baseUrl + url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(this.extractErrorMessage(text) || `HTTP ${response.status}: ${url}`);
    }
    return response.status === 204 ? undefined as T : response.json();
  }

  private async del<T>(url: string): Promise<T> {
    const response = await fetch(this.baseUrl + url, {
      method: 'DELETE',
      credentials: 'same-origin',
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(this.extractErrorMessage(text) || `HTTP ${response.status}: ${url}`);
    }
    return response.status === 204 ? undefined as T : response.json();
  }

  /** Try to extract a user-friendly error message from a response body that may be JSON */
  private extractErrorMessage(text: string): string {
    if (!text) return '';
    try {
      const json = JSON.parse(text);
      if (typeof json.error === 'string') return json.error;
    } catch {
      // Not JSON, return as-is
    }
    return text;
  }
}

export const apiClient = new ApiClient();
