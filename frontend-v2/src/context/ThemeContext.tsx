import { createContext, useContext, useEffect, useState, ReactNode } from 'react';

type Theme = 'light' | 'dark' | 'auto';

interface ThemeContextValue {
  theme: Theme;
  resolved: 'light' | 'dark';
  setTheme: (theme: Theme) => void;
}

const ThemeContext = createContext<ThemeContextValue>({
  theme: 'auto',
  resolved: 'light',
  setTheme: () => {},
});

export function useTheme() {
  return useContext(ThemeContext);
}

function getAutoTheme(): 'light' | 'dark' {
  // Check system preference first
  if (window.matchMedia('(prefers-color-scheme: dark)').matches) return 'dark';
  // Fallback to time-of-day
  const hour = new Date().getHours();
  return hour >= 6 && hour < 18 ? 'light' : 'dark';
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => {
    return (localStorage.getItem('ps-v2-theme') as Theme) || 'auto';
  });

  const [resolved, setResolved] = useState<'light' | 'dark'>(() => {
    if (theme !== 'auto') return theme;
    return getAutoTheme();
  });

  useEffect(() => {
    const resolve = () => {
      const r = theme === 'auto' ? getAutoTheme() : theme;
      setResolved(r);
      document.documentElement.classList.toggle('dark', r === 'dark');
    };
    resolve();

    // Listen for system theme changes
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const handler = () => { if (theme === 'auto') resolve(); };
    mq.addEventListener('change', handler);

    // Re-check every 5 minutes for time-of-day changes
    const interval = setInterval(() => { if (theme === 'auto') resolve(); }, 5 * 60 * 1000);

    return () => {
      mq.removeEventListener('change', handler);
      clearInterval(interval);
    };
  }, [theme]);

  const setTheme = (t: Theme) => {
    setThemeState(t);
    localStorage.setItem('ps-v2-theme', t);
  };

  return (
    <ThemeContext.Provider value={{ theme, resolved, setTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}
