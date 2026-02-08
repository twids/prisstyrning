import { Chip, CircularProgress, Tooltip } from '@mui/material';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import ErrorIcon from '@mui/icons-material/Error';
import { useAuth } from '../hooks/useAuth';
import { format } from 'date-fns';

export default function AuthStatusChip() {
  const { isAuthorized, expiresAt, isLoading } = useAuth();

  if (isLoading) {
    return <CircularProgress size={24} />;
  }

  if (!isAuthorized) {
    return (
      <Chip
        icon={<ErrorIcon />}
        label="Not Authorized"
        color="error"
        size="small"
      />
    );
  }

  const expiresAtDate = expiresAt ? new Date(expiresAt) : null;
  const tooltipText = expiresAtDate
    ? `Expires: ${format(expiresAtDate, 'PPpp')}`
    : 'Authorized';

  return (
    <Tooltip title={tooltipText}>
      <Chip
        icon={<CheckCircleIcon />}
        label="Authorized"
        color="success"
        size="small"
      />
    </Tooltip>
  );
}
