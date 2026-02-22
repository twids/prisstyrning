import { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../api/client';
import { useTheme } from '../context/ThemeContext';

interface LayoutProps {
  children: ReactNode;
}

export default function Layout({ children }: LayoutProps) {
  const location = useLocation();
  const { theme, setTheme, resolved } = useTheme();
  const adminStatusQuery = useQuery({
    queryKey: ['admin-status'],
    queryFn: () => apiClient.getAdminStatus(),
    staleTime: 5 * 60 * 1000,
  });
  const isAdmin = adminStatusQuery.data?.isAdmin ?? false;

  const navLinks = [
    { to: '/', label: 'Dashboard' },
    { to: '/settings', label: 'Settings' },
    ...(isAdmin ? [{ to: '/admin', label: 'Admin' }] : []),
  ];

  const cycleTheme = () => {
    const order: Array<'auto' | 'light' | 'dark'> = ['auto', 'light', 'dark'];
    const idx = order.indexOf(theme as 'auto' | 'light' | 'dark');
    setTheme(order[(idx + 1) % order.length]);
  };

  return (
    <div className="min-h-screen flex flex-col">
      <header className="sticky top-0 z-50 bg-white/80 dark:bg-gray-900/80 backdrop-blur-lg border-b border-gray-200 dark:border-gray-800">
        <div className="max-w-5xl mx-auto px-4 h-14 flex items-center justify-between">
          <div className="flex items-center gap-6">
            <Link to="/" className="text-lg font-semibold tracking-tight">
              Prisstyrning
            </Link>
            <nav className="hidden sm:flex items-center gap-1">
              {navLinks.map(({ to, label }) => (
                <Link
                  key={to}
                  to={to}
                  className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
                    location.pathname === to
                      ? 'bg-gray-100 dark:bg-gray-800 text-gray-900 dark:text-white'
                      : 'text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white hover:bg-gray-50 dark:hover:bg-gray-800/50'
                  }`}
                >
                  {label}
                </Link>
              ))}
            </nav>
          </div>
          <button
            onClick={cycleTheme}
            className="p-2 rounded-lg text-gray-500 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors"
            title={`Theme: ${theme}`}
          >
            {resolved === 'dark' ? (
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646Z" />
              </svg>
            ) : (
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 3v2.25m6.364.386l-1.591 1.591M21 12h-2.25m-.386 6.364l-1.591-1.591M12 18.75V21m-4.773-4.227l-1.591 1.591M5.25 12H3m4.227-4.773L5.636 5.636M15.75 12a3.75 3.75 0 11-7.5 0 3.75 3.75 0 017.5 0z" />
              </svg>
            )}
          </button>
        </div>
        {/* Mobile nav */}
        <div className="sm:hidden flex items-center gap-1 px-4 pb-2">
          {navLinks.map(({ to, label }) => (
            <Link
              key={to}
              to={to}
              className={`px-3 py-1.5 rounded-lg text-sm font-medium transition-colors ${
                location.pathname === to
                  ? 'bg-gray-100 dark:bg-gray-800 text-gray-900 dark:text-white'
                  : 'text-gray-600 dark:text-gray-400'
              }`}
            >
              {label}
            </Link>
          ))}
        </div>
      </header>
      <main className="flex-1 max-w-5xl w-full mx-auto px-4 py-6">
        {children}
      </main>
    </div>
  );
}
