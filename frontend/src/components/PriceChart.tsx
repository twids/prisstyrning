import {
  ResponsiveContainer,
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
} from 'recharts';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { usePrices } from '../hooks/usePrices';
import { useFormatters } from '../context/TimezoneContext';

export default function PriceChart() {
  const { data, isLoading, error } = usePrices();
  const { formatTime, formatDateTime } = useFormatters();

  if (isLoading) {
    return (
      <Card>
        <CardContent className="flex justify-center items-center p-8">
          <div className="h-8 w-8 animate-spin rounded-full border-4 border-primary border-t-transparent" />
        </CardContent>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <CardContent className="p-4">
          <p className="text-destructive text-sm">Failed to load price data: {error.message}</p>
        </CardContent>
      </Card>
    );
  }

  if (!data || data.items.length === 0) {
    return (
      <Card>
        <CardContent className="p-4">
          <p className="text-muted-foreground text-sm">No price data available</p>
        </CardContent>
      </Card>
    );
  }

  // Sort all items chronologically
  const allItems = [...data.items].sort(
    (a, b) => new Date(a.start).getTime() - new Date(b.start).getTime()
  );

  // Build chart data combining today + tomorrow
  const chartData = allItems.map((p) => ({
    time: new Date(p.start).getTime(),
    today: p.day === 'today' ? p.value : undefined,
    tomorrow: p.day === 'tomorrow' ? p.value : undefined,
  }));

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">Elpriser (24h)</CardTitle>
      </CardHeader>
      <CardContent className="pr-2">
        <ResponsiveContainer width="100%" height={240}>
          <AreaChart data={chartData} margin={{ top: 10, right: 0, left: -20, bottom: 0 }}>
            <defs>
              <linearGradient id="gradientToday" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="hsl(var(--destructive))" stopOpacity={0.8} />
                <stop offset="50%" stopColor="hsl(var(--warning))" stopOpacity={0.5} />
                <stop offset="100%" stopColor="hsl(var(--accent))" stopOpacity={0.3} />
              </linearGradient>
              <linearGradient id="gradientTomorrow" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="hsl(var(--primary))" stopOpacity={0.6} />
                <stop offset="100%" stopColor="hsl(var(--primary))" stopOpacity={0.1} />
              </linearGradient>
            </defs>
            <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" strokeOpacity={0.5} />
            <XAxis
              dataKey="time"
              type="number"
              scale="time"
              domain={['dataMin', 'dataMax']}
              tickFormatter={(v) => formatTime(v)}
              tick={{ fontSize: 11, fill: 'hsl(var(--muted-foreground))' }}
              tickLine={false}
              axisLine={false}
            />
            <YAxis
              tickFormatter={(v) => `${v}`}
              tick={{ fontSize: 11, fill: 'hsl(var(--muted-foreground))' }}
              tickLine={false}
              axisLine={false}
              unit=" öre"
            />
            <Tooltip
              contentStyle={{
                background: 'hsl(var(--popover))',
                border: '1px solid hsl(var(--border))',
                borderRadius: '8px',
                color: 'hsl(var(--popover-foreground))',
                fontSize: 12,
              }}
              labelFormatter={(v) => formatTime(v as number)}
              formatter={(value) => [`${value ?? ''} öre/kWh`]}
            />
            <Area
              type="monotone"
              dataKey="today"
              name="Idag"
              stroke="hsl(var(--accent))"
              fill="url(#gradientToday)"
              strokeWidth={2}
              dot={false}
              connectNulls={false}
            />
            <Area
              type="monotone"
              dataKey="tomorrow"
              name="Imorgon"
              stroke="hsl(var(--primary))"
              fill="url(#gradientTomorrow)"
              strokeWidth={2}
              dot={false}
              connectNulls={false}
            />
          </AreaChart>
        </ResponsiveContainer>
        <p className="text-xs text-muted-foreground mt-2">
          Senast uppdaterad: {data.updated ? formatDateTime(data.updated) : 'N/A'}
        </p>
      </CardContent>
    </Card>
  );
}
