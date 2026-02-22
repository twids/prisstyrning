import { useAuth } from '../hooks/useAuth';
import { format } from 'date-fns';

export default function AuthStatusBadge() {
  const { isAuthorized, expiresAt, isLoading } = useAuth();

  if (isLoading) {
    return <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 dark:bg-gray-800 text-gray-500 animate-pulse">Loading...</span>;
  }

  if (!isAuthorized) {
    return (
      <span className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium bg-red-100 dark:bg-red-900/30 text-red-700 dark:text-red-400">
        <span className="w-1.5 h-1.5 rounded-full bg-red-500" />
        Not Authorized
      </span>
    );
  }

  const expiresAtDate = expiresAt ? new Date(expiresAt) : null;
  const tooltipText = expiresAtDate ? `Expires: ${format(expiresAtDate, 'PPpp')}` : 'Authorized';

  return (
    <span className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium bg-green-100 dark:bg-green-900/30 text-green-700 dark:text-green-400" title={tooltipText}>
      <span className="w-1.5 h-1.5 rounded-full bg-green-500" />
      Authorized
    </span>
  );
}
