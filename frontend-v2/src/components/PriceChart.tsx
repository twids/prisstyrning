import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer, ReferenceLine } from 'recharts';
import Card from './Card';
import { usePrices } from '../hooks/usePrices';
import { useTheme } from '../context/ThemeContext';
import { formatTime, formatDateTime } from '../dateFormat';

interface PriceChartProps {
  threshold?: number | null;
}

export default function PriceChart({ threshold }: PriceChartProps) {
  const { data, isLoading, error } = usePrices();
  const { resolved: themeMode } = useTheme();

  if (isLoading) {
    return (
      <Card>
        <div className="h-72 flex items-center justify-center">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
        </div>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-4 text-sm text-red-600 dark:text-red-400">
          Failed to load price data: {error.message}
        </div>
      </Card>
    );
  }

  if (!data || data.items.length === 0) {
    return (
      <Card>
        <div className="bg-blue-50 dark:bg-blue-950 border border-blue-200 dark:border-blue-800 rounded-lg p-4 text-sm text-blue-600 dark:text-blue-400">
          No price data available
        </div>
      </Card>
    );
  }

  // Sort chronologically and create chart data
  const allItems = [...data.items].sort((a, b) => new Date(a.start).getTime() - new Date(b.start).getTime());

  const chartData = allItems.map(p => ({
    time: new Date(p.start).getTime(),
    label: formatTime(p.start),
    today: p.day === 'today' ? p.value : undefined,
    tomorrow: p.day === 'tomorrow' ? p.value : undefined,
  }));

  return (
    <Card>
      <h3 className="text-lg font-semibold mb-4">Electricity Prices (öre/kWh)</h3>
      <ResponsiveContainer width="100%" height={300}>
        <LineChart data={chartData} margin={{ top: 5, right: 20, bottom: 5, left: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke={themeMode === 'dark' ? '#374151' : '#e5e7eb'} />
          <XAxis
            dataKey="label"
            tick={{ fontSize: 12, fill: themeMode === 'dark' ? '#9ca3af' : '#6b7280' }}
            interval="preserveStartEnd"
          />
          <YAxis tick={{ fontSize: 12, fill: themeMode === 'dark' ? '#9ca3af' : '#6b7280' }} />
          <Tooltip
            contentStyle={{
              backgroundColor: themeMode === 'dark' ? '#1f2937' : '#fff',
              border: `1px solid ${themeMode === 'dark' ? '#374151' : '#e5e7eb'}`,
              borderRadius: '0.75rem',
              fontSize: '0.875rem',
              color: themeMode === 'dark' ? '#f3f4f6' : '#111827',
            }}
          />
          <Legend wrapperStyle={{ fontSize: '0.875rem', color: themeMode === 'dark' ? '#d1d5db' : '#374151' }} />
          <Line type="monotone" dataKey="today" name="Today" stroke="#3b82f6" strokeWidth={2} dot={false} connectNulls={false} />
          <Line type="monotone" dataKey="tomorrow" name="Tomorrow" stroke="#f59e0b" strokeWidth={2} dot={false} connectNulls={false} />
          {threshold != null && (
            <ReferenceLine 
              y={threshold} 
              stroke="#10b981" 
              strokeDasharray="6 4" 
              strokeWidth={1.5} 
              label={{
                value: `Target: ${threshold.toFixed(0)} öre`,
                position: 'insideTopRight',
                fill: themeMode === 'dark' ? '#6ee7b7' : '#059669',
                fontSize: 11,
              }}
            />
          )}
        </LineChart>
      </ResponsiveContainer>
      <p className="text-xs text-gray-500 mt-3">
        Last updated: {data.updated ? formatDateTime(data.updated) : 'N/A'}
      </p>
    </Card>
  );
}
