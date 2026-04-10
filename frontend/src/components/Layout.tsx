import { ReactNode } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Lightning, GearSix, ClockCounterClockwise, ShieldCheck } from '@phosphor-icons/react';
import { cn } from '@/lib/utils';
import { apiClient } from '../api/client';

interface LayoutProps {
  children: ReactNode;
}

const navItems = [
  { label: 'Dashboard', icon: Lightning, path: '/' },
  { label: 'Inställningar', icon: GearSix, path: '/settings' },
  { label: 'Historik', icon: ClockCounterClockwise, path: '/history' },
];

export default function Layout({ children }: LayoutProps) {
  const location = useLocation();
  const navigate = useNavigate();

  const adminStatusQuery = useQuery({
    queryKey: ['admin-status'],
    queryFn: () => apiClient.getAdminStatus(),
    staleTime: 5 * 60 * 1000,
  });

  const isAdmin = adminStatusQuery.data?.isAdmin ?? false;

  const allNavItems = isAdmin
    ? [...navItems, { label: 'Admin', icon: ShieldCheck, path: '/admin' }]
    : navItems;

  return (
    <div className="flex flex-col min-h-screen">
      {/* Header */}
      <header className="sticky top-0 z-40 border-b border-border bg-background/80 backdrop-blur-lg px-4 h-14 flex items-center gap-2">
        <Lightning size={22} weight="fill" className="text-primary" />
        <span className="font-semibold text-lg tracking-tight">Prisstyrning</span>
      </header>

      {/* Main content */}
      <main className="flex-1 px-4 py-4 pb-24 max-w-2xl w-full mx-auto">
        {children}
      </main>

      {/* Bottom tab bar */}
      <nav className="fixed bottom-0 left-0 right-0 z-50 bg-background/80 backdrop-blur-lg border-t border-border">
        <div className="flex items-stretch h-16 max-w-2xl mx-auto">
          {allNavItems.map(({ label, icon: Icon, path }) => {
            const isActive = path === '/'
              ? location.pathname === '/'
              : location.pathname.startsWith(path);
            return (
              <button
                key={path}
                onClick={() => navigate(path)}
                className={cn(
                  'flex flex-1 flex-col items-center justify-center gap-0.5 transition-colors',
                  isActive ? 'text-primary' : 'text-muted-foreground hover:text-foreground',
                )}
              >
                <Icon size={22} weight={isActive ? 'fill' : 'regular'} />
                <span className="text-xs font-medium leading-none">{label}</span>
              </button>
            );
          })}
        </div>
      </nav>
    </div>
  );
}
