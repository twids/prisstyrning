import { Paper, Skeleton, Stack } from '@mui/material';

export default function LoadingSkeleton() {
  return (
    <Stack spacing={3}>
      <Paper sx={{ p: 3 }}>
        <Skeleton variant="text" width="40%" height={40} />
        <Skeleton variant="rectangular" height={200} sx={{ mt: 2 }} />
      </Paper>
      <Paper sx={{ p: 3 }}>
        <Skeleton variant="text" width="30%" height={40} />
        <Skeleton variant="rectangular" height={150} sx={{ mt: 2 }} />
      </Paper>
      <Paper sx={{ p: 3 }}>
        <Skeleton variant="text" width="50%" height={40} />
        <Skeleton variant="rectangular" height={100} sx={{ mt: 2 }} />
      </Paper>
    </Stack>
  );
}
