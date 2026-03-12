import { LineChart } from '@mui/x-charts/LineChart';
import { Card, CardContent, Typography, Box } from '@mui/material';
import { usePriceTrend } from '../hooks/usePriceTrend';

export default function TrendChart() {
  const { data, isLoading, error } = usePriceTrend();

  if (isLoading || error || !data || data.dailyAverages.length === 0) {
    return null;
  }

  const trendInfo = data.trendFactor < 0.9
    ? { icon: '↓', text: 'Falling', color: 'success.main' }
    : data.trendFactor > 1.1
    ? { icon: '↑', text: 'Rising', color: 'error.main' }
    : { icon: '→', text: 'Stable', color: 'text.secondary' };

  const xData = data.dailyAverages.map(d => new Date(d.date + 'T00:00:00'));
  const yData = data.dailyAverages.map(d => Math.round(d.avgPrice * 10) / 10);

  return (
    <Card>
      <CardContent>
        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
          <Typography variant="h6">
            Price Trend ({data.lookbackDays} days)
          </Typography>
          <Typography variant="body2" fontWeight="medium" color={trendInfo.color}>
            {trendInfo.icon} {trendInfo.text} ({data.trendFactor.toFixed(2)}x)
          </Typography>
        </Box>
        <LineChart
          xAxis={[
            {
              data: xData,
              scaleType: 'time',
              valueFormatter: (value: Date) =>
                `${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`,
            },
          ]}
          series={[
            {
              label: 'Avg Price',
              data: yData,
              color: '#4FC3F7',
              showMark: false,
              curve: 'monotoneX',
              area: true,
            },
          ]}
          height={200}
          margin={{ top: 20, right: 20, bottom: 40, left: 60 }}
        />
        <Typography variant="caption" color="text.secondary">
          Daily average prices for {data.zone}. Trend = 7-day avg ÷ 30-day avg.
        </Typography>
      </CardContent>
    </Card>
  );
}
