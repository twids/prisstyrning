import { createContext, useContext, useMemo, useState, useEffect, ReactNode } from 'react';
import {
  formatDateTime as rawFormatDateTime,
  formatTime as rawFormatTime,
  formatDateTimeFull as rawFormatDateTimeFull,
} from '../dateFormat';

interface TimezoneContextValue {
  timezone: string;  // resolved IANA timezone
  setting: string;   // normalized timezone setting ("auto" or resolved IANA timezone)
  profile: LocaleProfileKey; // selected locale profile key (raw user choice)
}

export type LocaleProfileKey = 'auto' | 'se' | 'no' | 'dk' | 'fi';

interface LocaleOption {
  value: LocaleProfileKey;
  label: string;
  zoneLabel: string;
  locale?: string;
  timezone?: string;
}

interface LocaleContextValue {
  localeSetting: LocaleProfileKey;
  setLocaleSetting: (locale: LocaleProfileKey) => void;
  systemLocale: string;
  systemTimezone: string;
  locale: string | undefined; // resolved locale, undefined = browser default
  effectiveLocale: string;
  effectiveTimezone: string;
  localeOptions: readonly LocaleOption[];
  selectedOption: LocaleOption;
}

const LOCALE_STORAGE_KEY = 'prisstyrning-locale-setting';

const LOCALE_OPTIONS: readonly LocaleOption[] = [
  { value: 'auto', label: 'Auto (System)', zoneLabel: 'System/Browser defaults' },
  {
    value: 'se',
    label: 'Sweden (SE1-SE4)',
    zoneLabel: 'SE1-SE4',
    locale: 'sv-SE',
    timezone: 'Europe/Stockholm',
  },
  {
    value: 'no',
    label: 'Norway (NO1-NO5)',
    zoneLabel: 'NO1-NO5',
    locale: 'nb-NO',
    timezone: 'Europe/Oslo',
  },
  {
    value: 'dk',
    label: 'Denmark (DK1-DK2)',
    zoneLabel: 'DK1-DK2',
    locale: 'da-DK',
    timezone: 'Europe/Copenhagen',
  },
  {
    value: 'fi',
    label: 'Finland (FI)',
    zoneLabel: 'FI',
    locale: 'fi-FI',
    timezone: 'Europe/Helsinki',
  },
];

function isLocaleProfileKey(value: string): value is LocaleProfileKey {
  return LOCALE_OPTIONS.some((option) => option.value === value);
}

function normalizeLocaleProfileKey(value: string): LocaleProfileKey {
  return isLocaleProfileKey(value) ? value : 'auto';
}

function getSystemTimezone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

function getSystemLocale(): string {
  if (typeof navigator !== 'undefined' && navigator.language) {
    return navigator.language;
  }
  return Intl.DateTimeFormat().resolvedOptions().locale || 'en-US';
}

const TimezoneContext = createContext<TimezoneContextValue>({
  timezone: getSystemTimezone(),
  setting: 'auto',
  profile: 'auto',
});

const LocaleContext = createContext<LocaleContextValue>({
  localeSetting: 'auto',
  setLocaleSetting: () => undefined,
  systemLocale: getSystemLocale(),
  systemTimezone: getSystemTimezone(),
  locale: undefined,
  effectiveLocale: getSystemLocale(),
  effectiveTimezone: getSystemTimezone(),
  localeOptions: LOCALE_OPTIONS,
  selectedOption: LOCALE_OPTIONS[0],
});

export function TimezoneProvider({ children }: { children: ReactNode }) {
  const [localeSetting, setLocaleSettingState] = useState<LocaleProfileKey>(() => {
    if (typeof window === 'undefined') {
      return 'auto';
    }

    try {
      const stored = window.localStorage.getItem(LOCALE_STORAGE_KEY);
      return stored && stored.trim() ? normalizeLocaleProfileKey(stored.trim()) : 'auto';
    } catch {
      return 'auto';
    }
  });

  const systemLocale = useMemo(() => getSystemLocale(), []);
  const systemTimezone = useMemo(() => getSystemTimezone(), []);
  const selectedOption = useMemo(
    () => LOCALE_OPTIONS.find((option) => option.value === localeSetting) ?? LOCALE_OPTIONS[0],
    [localeSetting],
  );

  const setLocaleSetting = (locale: LocaleProfileKey) => {
    setLocaleSettingState(locale);
  };

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(LOCALE_STORAGE_KEY, localeSetting);
    } catch {
      // Ignore storage failures (e.g., private mode restrictions)
    }
  }, [localeSetting]);

  const safeLocale = useMemo(() => {
    if (selectedOption.value === 'auto' || !selectedOption.locale) {
      return undefined;
    }

    try {
      // Validate BCP-47 locale.
      // eslint-disable-next-line no-new
      new Intl.DateTimeFormat(selectedOption.locale);
      return selectedOption.locale;
    } catch {
      return undefined;
    }
  }, [selectedOption]);

  const safeTimezone = useMemo(() => {
    if (selectedOption.value === 'auto' || !selectedOption.timezone) {
      return systemTimezone;
    }

    try {
      // Validate that timezone is a supported IANA timezone. This will throw on invalid values.
      // eslint-disable-next-line no-new
      new Intl.DateTimeFormat(undefined, { timeZone: selectedOption.timezone });
      return selectedOption.timezone;
    } catch {
      return systemTimezone;
    }
  }, [selectedOption, systemTimezone]);

  const timezoneValue = useMemo(() => {
    const setting = selectedOption.value === 'auto' ? 'auto' : safeTimezone;

    return {
      timezone: safeTimezone,
      setting,
      profile: selectedOption.value,
    };
  }, [selectedOption.value, safeTimezone]);

  const localeValue = useMemo(() => {
    const effectiveLocale = safeLocale ?? systemLocale;
    const effectiveTimezone = safeTimezone;

    return {
      localeSetting,
      setLocaleSetting,
      systemLocale,
      systemTimezone,
      locale: safeLocale,
      effectiveLocale,
      effectiveTimezone,
      localeOptions: LOCALE_OPTIONS,
      selectedOption,
    };
  }, [localeSetting, safeLocale, safeTimezone, selectedOption, systemLocale, systemTimezone]);

  return (
    <TimezoneContext.Provider value={timezoneValue}>
      <LocaleContext.Provider value={localeValue}>
        {children}
      </LocaleContext.Provider>
    </TimezoneContext.Provider>
  );
}

export function useTimezone() {
  return useContext(TimezoneContext);
}

export function useLocale() {
  return useContext(LocaleContext);
}

export function useFormatters() {
  const { timezone } = useTimezone();
  const { locale } = useLocale();

  return useMemo(() => ({
    formatDateTime: (date: Date | string | number) => rawFormatDateTime(date, timezone, locale),
    formatTime: (date: Date | string | number) => rawFormatTime(date, timezone, locale),
    formatDateTimeFull: (date: Date | string | number) => rawFormatDateTimeFull(date, timezone, locale),
  }), [timezone, locale]);
}
