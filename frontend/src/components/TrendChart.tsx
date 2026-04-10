import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
} from 'recharts';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { usePriceTrend } from '../hooks/usePriceTrend';

export default function TrendChart() {
  const { data, isLoading, error } = usePriceTrend();

  if (isLoading || error || !data || data.dailyAverages.length === 0) {
    return null;
  }

  const trendInfo =
    data.trendFactor < 0.9
      ? { icon: '↓', label: 'Sjunkande', className: 'text-green-500' }
      : data.trendFactor > 1.1
      ? { icon: '↑', label: 'Stigande', className: 'text-red-500' }
      : { icon: '→', label: 'Stabil', className: 'text-muted-foreground' };

  const chartData = data.dailyAverages.map((d) => {
    const dt = new Date(d.date + 'T00:00:00');
    const mm = String(dt.getMonth() + 1).padStart(2, '0');
    const dd = String(dt.getDate()).padStart(2, '0');
    return { date: `${mm}-${dd}`, value: Math.round(d.avgPrice * 10) / 10 };
  });

  return (
    <Card>
      <CardHeader className="pb-2">
        <div className="flex items-center justify-between">
          <CardTitle className="text-base">Pristrend</CardTitle>
          <span className={`text-sm font-medium ${trendInfo.className}`}>
            {trendInfo.icon} {trendInfo.label} ({data.trendFactor.toFixed(2)}x)
          </span>
        </div>
      </CardHeader>
      <CardContent className="pb-2">
        <ResponsiveContainer width="100%" height={200}>
          <AreaChart data={chartData} margin={{ top: 10, right: 10, bottom: 0, left: 0 }}>
            <defs>
              <linearGradient id="trendGradient" x1="0" y1="0" x2="0" y2="1">
                <stop offset="5%" stopColor="hsl(var(--primary))" stopOpacity={0.3} />
                <stop offset="95%" stopColor="hsl(var(--primary))" stopOpacity={0} />
              </linearGradient>
            </defs>
            <XAxis
              dataKey="date"
              tick={{ fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              interval="preserveStartEnd"
            />
            <YAxis
              tick={{ fontSize: 11 }}
              tickLine={false}
              axisLine={false}
              tickFormatter={(v) => `${v}`}
              unit=" öre"
              width={60}
            />
            <Tooltip
              contentStyle={{ fontSize: 12 }}
              formatter={(value) => [`${value} öre/kWh`, 'Snitt']}
            />
            <Area
              type="monotone"
              dataKey="value"
              name="Snitt"
              stroke="hsl(var(--primary))"
              strokeWidth={2}
              fill="url(#trendGradient)"
              dot={false}
            />
          </AreaChart>
        </ResponsiveContainer>
        <p className="text-xs text-muted-foreground mt-1">
          Dagligt snittpris för {data.zone}. Trend = 7d snitt ÷ 30d snitt.
        </p>
      </CardContent>
    </Card>
  );
}
