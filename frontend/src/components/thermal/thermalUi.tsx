import type { ReactNode } from 'react';
import { Box, Chip, Paper, Skeleton, Stack, Typography } from '@mui/material';
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';
import ScheduleIcon from '@mui/icons-material/Schedule';
import type { DataQuality } from '../../types/api';

export const modeLabel = {
  Legacy: 'Legacy',
  Shadow: 'Shadow',
  LwtActive: 'LWT aktiv',
  FullActive: 'Fullt aktiv',
} as const;

export function formatRelative(value: string | null | undefined): string {
  if (!value) return 'aldrig';
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  if (!Number.isFinite(seconds)) return 'okänd tid';
  const formatter = new Intl.RelativeTimeFormat('sv-SE', { numeric: 'auto' });
  if (Math.abs(seconds) < 90) return formatter.format(seconds, 'second');
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 90) return formatter.format(minutes, 'minute');
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 36) return formatter.format(hours, 'hour');
  return formatter.format(Math.round(hours / 24), 'day');
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '–';
  return new Intl.DateTimeFormat('sv-SE', {
    dateStyle: 'short',
    timeStyle: 'short',
    timeZone: 'Europe/Stockholm',
  }).format(new Date(value));
}

export function QualityChip({ quality }: { quality: DataQuality }) {
  const settings = {
    Valid: { label: 'Giltig', color: 'success' as const, icon: <CheckCircleOutlineIcon /> },
    Stale: { label: 'Gammal', color: 'warning' as const, icon: <ScheduleIcon /> },
    Invalid: { label: 'Ogiltig', color: 'error' as const, icon: <ErrorOutlineIcon /> },
    Unavailable: { label: 'Saknas', color: 'default' as const, icon: <ErrorOutlineIcon /> },
  }[quality];
  return <Chip size="small" variant="outlined" {...settings} />;
}

export function PageHeader({ eyebrow, title, description, action }: {
  eyebrow?: string;
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" alignItems={{ md: 'flex-end' }} gap={2}>
      <Box sx={{ maxWidth: 760 }}>
        {eyebrow && <Typography variant="overline" color="primary.main" fontWeight={800}>{eyebrow}</Typography>}
        <Typography variant="h3" component="h1" sx={{ fontSize: { xs: '2rem', md: '2.6rem' }, letterSpacing: '-0.04em' }}>
          {title}
        </Typography>
        <Typography color="text.secondary" sx={{ mt: 1, fontSize: '1.05rem' }}>{description}</Typography>
      </Box>
      {action}
    </Stack>
  );
}

export function MetricCard({ label, value, detail, icon, accent = '#69d4c0', loading = false }: {
  label: string;
  value: ReactNode;
  detail?: ReactNode;
  icon?: ReactNode;
  accent?: string;
  loading?: boolean;
}) {
  return (
    <Paper variant="outlined" sx={{ p: 2.5, height: '100%', position: 'relative', overflow: 'hidden' }}>
      <Box aria-hidden sx={{ position: 'absolute', inset: '0 auto 0 0', width: 3, bgcolor: accent }} />
      <Stack direction="row" justifyContent="space-between" alignItems="flex-start" gap={2}>
        <Box>
          <Typography variant="body2" color="text.secondary" fontWeight={650}>{label}</Typography>
          {loading ? <Skeleton width={100} height={44} /> : <Typography variant="h4" component="p" sx={{ mt: .5, letterSpacing: '-0.03em' }}>{value}</Typography>}
          {detail && <Typography variant="body2" color="text.secondary" sx={{ mt: .7 }}>{detail}</Typography>}
        </Box>
        {icon && <Box sx={{ color: accent, opacity: .9 }}>{icon}</Box>}
      </Stack>
    </Paper>
  );
}
