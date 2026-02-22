import { Box, Typography, Stack } from '@mui/material';
import CircleIcon from '@mui/icons-material/Circle';

export default function ScheduleLegend() {
  return (
    <Stack direction="row" spacing={3} sx={{ mt: 2 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <CircleIcon sx={{ color: 'success.main', fontSize: 16 }} />
        <Typography variant="body2">Comfort (Heating)</Typography>
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <CircleIcon sx={{ color: 'info.main', fontSize: 16 }} />
        <Typography variant="body2">Eco (Daily DHW)</Typography>
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <CircleIcon sx={{ color: 'error.main', fontSize: 16 }} />
        <Typography variant="body2">Turn Off (No Heating)</Typography>
      </Box>
    </Stack>
  );
}
