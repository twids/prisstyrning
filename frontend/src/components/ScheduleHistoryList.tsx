import { useState } from 'react';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { useFormatters } from '../context/TimezoneContext';
import { useScheduleHistory } from '../hooks/useScheduleHistory';
import HeatingTimeline from './HeatingTimeline';

export default function ScheduleHistoryList() {
  const { formatDateTimeFull } = useFormatters();
  const { data, isLoading, error } = useScheduleHistory();
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null);

  if (isLoading) {
    return (
      <div className="flex justify-center p-6">
        <div className="h-6 w-6 animate-spin rounded-full border-2 border-primary border-t-transparent" />
      </div>
    );
  }

  if (error) {
    return (
      <p className="text-sm text-destructive">
        Misslyckades med att ladda schemahistorik: {error.message}
      </p>
    );
  }

  if (!data || data.length === 0) {
    return (
      <p className="text-sm text-muted-foreground">Ingen schemahistorik ännu.</p>
    );
  }

  return (
    <div className="space-y-2">
      {data.map((entry, index) => {
        const isExpanded = expandedIndex === index;
        const timestamp = new Date(entry.timestamp);

        return (
          <Card
            key={index}
            className="cursor-pointer transition-colors hover:bg-muted/40"
            role="button"
            tabIndex={0}
            aria-expanded={isExpanded}
            onClick={() => setExpandedIndex(isExpanded ? null : index)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                setExpandedIndex(isExpanded ? null : index);
              }
            }}
          >
            <CardContent className="py-3 px-4 space-y-2">
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-medium">{formatDateTimeFull(timestamp)}</span>
                <Badge variant="secondary">{entry.date}</Badge>
              </div>
              <HeatingTimeline schedulePayload={entry.schedule} compact={!isExpanded} />
            </CardContent>
          </Card>
        );
      })}
    </div>
  );
}
