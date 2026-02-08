import { useState } from 'react';
import {
  Accordion,
  AccordionSummary,
  AccordionDetails,
  Typography,
  Box,
  Chip,
  CircularProgress,
  Alert,
} from '@mui/material';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { format } from 'date-fns';
import { useScheduleHistory } from '../hooks/useScheduleHistory';
import ScheduleGrid from './ScheduleGrid';

export default function ScheduleHistoryList() {
  const { data, isLoading, error } = useScheduleHistory();
  const [expanded, setExpanded] = useState<string | false>(false);

  const handleChange = (panel: string) => (_: React.SyntheticEvent, isExpanded: boolean) => {
    setExpanded(isExpanded ? panel : false);
  };

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', p: 3 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (error) {
    return (
      <Alert severity="error">
        Failed to load schedule history: {error.message}
      </Alert>
    );
  }

  if (!data || data.length === 0) {
    return (
      <Alert severity="info">
        No schedule history found. Generate a schedule to see it here.
      </Alert>
    );
  }

  return (
    <Box>
      {data.map((entry, index) => {
        const panelId = `history-${index}`;
        const timestamp = new Date(entry.timestamp);
        
        return (
          <Accordion
            key={panelId}
            expanded={expanded === panelId}
            onChange={handleChange(panelId)}
            sx={{ mb: 1 }}
          >
            <AccordionSummary expandIcon={<ExpandMoreIcon />}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, width: '100%' }}>
                <Typography>
                  {format(timestamp, 'PPpp')}
                </Typography>
                <Chip label={entry.date} size="small" />
              </Box>
            </AccordionSummary>
            <AccordionDetails>
              <ScheduleGrid schedulePayload={entry.schedule} />
            </AccordionDetails>
          </Accordion>
        );
      })}
    </Box>
  );
}
