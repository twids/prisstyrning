import { createContext, useContext, useMemo, ReactNode } from 'react';
import { useUserSettings } from '../hooks/useUserSettings';
import {
  formatDateTime as rawFormatDateTime,
  formatTime as rawFormatTime,
  formatDateTimeFull as rawFormatDateTimeFull,
} from '../dateFormat';

interface TimezoneContextValue {
  timezone: string;  // resolved IANA timezone
  setting: string;   // raw setting ("auto" or IANA)
}

const TimezoneContext = createContext<TimezoneContextValue>({
  timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  setting: 'auto',
});

export function TimezoneProvider({ children }: { children: ReactNode }) {
  const { settings } = useUserSettings();

  const value = useMemo(() => {
    const raw = settings?.Timezone ?? 'auto';
    const resolved = raw === 'auto'
      ? Intl.DateTimeFormat().resolvedOptions().timeZone
      : raw;
    return { timezone: resolved, setting: raw };
  }, [settings?.Timezone]);

  return (
    <TimezoneContext.Provider value={value}>
      {children}
    </TimezoneContext.Provider>
  );
}

export function useTimezone() {
  return useContext(TimezoneContext);
}

export function useFormatters() {
  const { timezone } = useTimezone();
  return useMemo(() => ({
    formatDateTime: (date: Date | string | number) => rawFormatDateTime(date, timezone),
    formatTime: (date: Date | string | number) => rawFormatTime(date, timezone),
    formatDateTimeFull: (date: Date | string | number) => rawFormatDateTimeFull(date, timezone),
  }), [timezone]);
}
