import { AreaChart, Area, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import Card from './Card';
import { usePriceTrend } from '../hooks/usePriceTrend';
import { useTheme } from '../context/ThemeContext';

export default function TrendChart() {
  const { data, isLoading, error } = usePriceTrend();
  const { resolved: themeMode } = useTheme();

  if (isLoading || error || !data || data.dailyAverages.length === 0) {
    return null; // Don't render anything if no trend data
  }

  const chartData = data.dailyAverages.map(d => ({
    date: d.date.slice(5), // "MM-DD" format
    avgPrice: Math.round(d.avgPrice * 10) / 10, // 1 decimal
  }));

  const trendInfo = data.trendFactor < 0.9
    ? { icon: '↓', text: 'Falling', color: 'text-green-600 dark:text-green-400' }
    : data.trendFactor > 1.1
    ? { icon: '↑', text: 'Rising', color: 'text-red-600 dark:text-red-400' }
    : { icon: '→', text: 'Stable', color: 'text-gray-500 dark:text-gray-400' };

  return (
    <Card>
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-lg font-semibold">Price Trend ({data.lookbackDays} days)</h3>
        <span className={`text-sm font-medium ${trendInfo.color}`}>
          {trendInfo.icon} {trendInfo.text} ({data.trendFactor.toFixed(2)}x)
        </span>
      </div>
      <ResponsiveContainer width="100%" height={150}>
        <AreaChart data={chartData} margin={{ top: 5, right: 20, bottom: 5, left: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke={themeMode === 'dark' ? '#374151' : '#e5e7eb'} />
          <XAxis 
            dataKey="date" 
            tick={{ fontSize: 10, fill: themeMode === 'dark' ? '#9ca3af' : '#6b7280' }}
            interval="preserveStartEnd"
          />
          <YAxis 
            tick={{ fontSize: 10, fill: themeMode === 'dark' ? '#9ca3af' : '#6b7280' }}
            width={40}
          />
          <Tooltip
            contentStyle={{
              backgroundColor: themeMode === 'dark' ? '#1f2937' : '#fff',
              border: `1px solid ${themeMode === 'dark' ? '#374151' : '#e5e7eb'}`,
              borderRadius: '0.5rem',
              fontSize: '0.75rem',
              color: themeMode === 'dark' ? '#f3f4f6' : '#111827',
            }}
            formatter={(value: number) => [`${value} öre/kWh`, 'Avg Price']}
          />
          <defs>
            <linearGradient id="priceGradient" x1="0" y1="0" x2="0" y2="1">
              <stop offset="5%" stopColor="#3b82f6" stopOpacity={0.3}/>
              <stop offset="95%" stopColor="#3b82f6" stopOpacity={0.05}/>
            </linearGradient>
          </defs>
          <Area type="monotone" dataKey="avgPrice" stroke="#3b82f6" strokeWidth={1.5} fill="url(#priceGradient)" />
        </AreaChart>
      </ResponsiveContainer>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
        Daily average prices for {data.zone}. Trend = 7-day avg ÷ 30-day avg.
      </p>
    </Card>
  );
}
