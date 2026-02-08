import { LineChart } from '@mui/x-charts/LineChart';
import { Card, CardContent, Typography, CircularProgress, Alert } from '@mui/material';
import { usePrices } from '../hooks/usePrices';

export default function PriceChart() {
  const { data, isLoading, error } = usePrices();

  if (isLoading) {
    return (
      <Card>
        <CardContent sx={{ display: 'flex', justifyContent: 'center', p: 4 }}>
          <CircularProgress />
        </CardContent>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <CardContent>
          <Alert severity="error">
            Failed to load price data: {error.message}
          </Alert>
        </CardContent>
      </Card>
    );
  }

  if (!data || data.items.length === 0) {
    return (
      <Card>
        <CardContent>
          <Alert severity="info">No price data available</Alert>
        </CardContent>
      </Card>
    );
  }

  // Sort all items chronologically to form the X-axis
  const allItems = [...data.items].sort((a, b) => 
    new Date(a.start).getTime() - new Date(b.start).getTime()
  );
  
  const xAxisData = allItems.map(p => new Date(p.start).getTime());
  
  // Create series data matching the X-axis length
  // Use null for points that don't belong to the series
  const todayData = allItems.map((p) => 
    p.day === 'today' ? p.value : null
  );

  const tomorrowData = allItems.map((p) => 
    p.day === 'tomorrow' ? p.value : null
  );

  return (
    <Card>
      <CardContent>
        <Typography variant="h6" gutterBottom>
          Electricity Prices (öre/kWh)
        </Typography>
        <LineChart
          xAxis={[
            {
              data: xAxisData,
              scaleType: 'time',
              valueFormatter: (value: number) =>
                new Date(value).toLocaleTimeString('sv-SE', {
                  hour: '2-digit',
                  minute: '2-digit',
                }),
            },
          ]}
          series={[
            {
              label: 'Today',
              data: todayData,
              color: '#4FC3F7', // Primary color
              showMark: false,
              curve: 'monotoneX',
              connectNulls: false,
            },
            {
              label: 'Tomorrow',
              data: tomorrowData,
              color: '#FFB74D', // Secondary color
              showMark: false,
              curve: 'monotoneX',
              connectNulls: false,
            },
          ]}
          height={300}
          margin={{ top: 20, right: 20, bottom: 40, left: 60 }}
        />
        <Typography variant="caption" color="text.secondary" sx={{ mt: 2 }}>
          Last updated: {data.updated ? new Date(data.updated).toLocaleString('sv-SE') : 'N/A'}
        </Typography>
      </CardContent>
    </Card>
  );
}
