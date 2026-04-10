import { Badge } from '@/components/ui/badge';
import { CheckCircle, WarningCircle } from '@phosphor-icons/react';
import { useAuth } from '../hooks/useAuth';

export default function ConnectionBadge() {
  const { isAuthorized } = useAuth();

  if (isAuthorized) {
    return (
      <Badge variant="success" className="flex items-center gap-1">
        <CheckCircle weight="fill" className="w-3.5 h-3.5" />
        Daikin Ansluten
      </Badge>
    );
  }

  return (
    <Badge variant="destructive" className="flex items-center gap-1">
      <WarningCircle weight="fill" className="w-3.5 h-3.5" />
      Ej ansluten
    </Badge>
  );
}
