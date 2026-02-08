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

  // Status
  async getStatus(): Promise<T.StatusResponse> {
    return this.get('/api/status');
  }

  // Helper methods
  private async get<T>(url: string): Promise<T> {
    const response = await fetch(this.baseUrl + url, {
      credentials: 'same-origin', // Include cookies for ps_user
    });
    if (!response.ok) {
      const text = await response.text();
      throw new Error(text || `HTTP ${response.status}: ${url}`);
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
      throw new Error(text || `HTTP ${response.status}: ${url}`);
    }
    return response.json();
  }
}

export const apiClient = new ApiClient();
