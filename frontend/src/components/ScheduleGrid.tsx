import { Box, Paper, Typography, styled } from '@mui/material';
import type { SchedulePayload, ScheduleState } from '../types/api';

const GridContainer = styled(Box)(({ theme }) => ({
  display: 'grid',
  gridTemplateColumns: 'auto repeat(24, 1fr)',
  gap: theme.spacing(0.25),
  alignItems: 'center',
}));

const HourHeader = styled(Box)(({ theme }) => ({
  textAlign: 'center',
  fontSize: '0.75rem',
  color: theme.palette.text.secondary,
  padding: theme.spacing(0.5),
}));

const DayLabel = styled(Box)(({ theme }) => ({
  fontSize: '0.875rem',
  fontWeight: 500,
  padding: theme.spacing(0.5),
  textAlign: 'right',
  paddingRight: theme.spacing(1),
}));

interface CellProps {
  state?: ScheduleState;
  isCurrentHour?: boolean;
}

const Cell = styled(Box, {
  shouldForwardProp: (prop) => prop !== 'state' && prop !== 'isCurrentHour',
})<CellProps>(({ theme, state, isCurrentHour }) => ({
  height: 32,
  border: `1px solid ${theme.palette.divider}`,
  borderRadius: theme.shape.borderRadius,
  position: 'relative',
  backgroundColor:
    state === 'comfort'
      ? theme.palette.success.main
      : state === 'eco'
      ? theme.palette.info.main
      : state === 'turn_off'
      ? theme.palette.error.main
      : theme.palette.action.disabledBackground,
  outline: isCurrentHour ? `2px solid ${theme.palette.primary.main}` : 'none',
  boxShadow: isCurrentHour ? `0 0 6px ${theme.palette.primary.main}` : 'none',
  transition: 'all 0.2s',
  '&:hover': {
    transform: 'scale(1.05)',
  },
}));

interface ScheduleGridProps {
  schedulePayload: SchedulePayload | null;
}

export default function ScheduleGrid({ schedulePayload }: ScheduleGridProps) {
  if (!schedulePayload) {
    return (
      <Paper sx={{ p: 2 }}>
        <Typography color="text.secondary">No schedule available</Typography>
      </Paper>
    );
  }

  // Parse schedule payload (complex logic - see current ui.js for reference)
  // Extract 24x2 grid of states for today and tomorrow
  
  // Get current hour for highlighting
  const now = new Date();
  const currentHour = now.getHours();
  const currentDay = now.getDay(); // 0 = Sunday, 6 = Saturday

  // Simplified parsing (TODO: Handle all schedule formats)
  const scheduleId = Object.keys(schedulePayload)[0];
  const schedule = schedulePayload[scheduleId];
  const actions = schedule?.actions || {};

  // Map day names
  const dayNames = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];
  const todayName = dayNames[currentDay];
  const tomorrowName = dayNames[(currentDay + 1) % 7];

  // Extract today's states
  const todayActions = actions[todayName] || {};
  const todayStates: Array<ScheduleState | undefined> = Array.from({ length: 24 }, (_, hour) => {
    const timeKey = `${hour.toString().padStart(2, '0')}:00:00`;
    return todayActions[timeKey]?.domesticHotWaterTemperature as ScheduleState | undefined;
  });

  // Extract tomorrow's states
  const tomorrowActions = actions[tomorrowName] || {};
  const tomorrowStates: Array<ScheduleState | undefined> = Array.from({ length: 24 }, (_, hour) => {
    const timeKey = `${hour.toString().padStart(2, '0')}:00:00`;
    return tomorrowActions[timeKey]?.domesticHotWaterTemperature as ScheduleState | undefined;
  });

  return (
    <Paper sx={{ p: 2 }}>
      <GridContainer>
        {/* Header row */}
        <Box /> {/* Empty corner */}
        {Array.from({ length: 24 }, (_, i) => (
          <HourHeader key={i}>{i}</HourHeader>
        ))}

        {/* Today row */}
        <DayLabel>Today</DayLabel>
        {todayStates.map((state, hour) => (
          <Cell key={`today-${hour}`} state={state} isCurrentHour={hour === currentHour} />
        ))}

        {/* Tomorrow row */}
        <DayLabel>Tomorrow</DayLabel>
        {tomorrowStates.map((state, hour) => (
          <Cell key={`tomorrow-${hour}`} state={state} />
        ))}
      </GridContainer>
    </Paper>
  );
}
