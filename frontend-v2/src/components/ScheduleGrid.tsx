import Card from './Card';
import type { SchedulePayload, ScheduleState } from '../types/api';

interface ScheduleGridProps {
  schedulePayload: SchedulePayload | null;
}

const stateStyles: Record<string, string> = {
  comfort: 'bg-green-500',
  eco: 'bg-blue-500',
  turn_off: 'bg-red-500',
};

export default function ScheduleGrid({ schedulePayload }: ScheduleGridProps) {
  if (!schedulePayload) {
    return (
      <Card><p className="text-gray-500 dark:text-gray-400 text-sm">No schedule available</p></Card>
    );
  }

  const now = new Date();
  const currentHour = now.getHours();
  const currentDay = now.getDay();
  const dayNames = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];
  const todayName = dayNames[currentDay];
  const tomorrowName = dayNames[(currentDay + 1) % 7];

  const scheduleId = Object.keys(schedulePayload)[0];
  const schedule = schedulePayload[scheduleId];
  const actions = schedule?.actions || {};

  const getStates = (dayName: string): Array<ScheduleState | undefined> => {
    const dayActions = actions[dayName] || {};
    return Array.from({ length: 24 }, (_, hour) => {
      const timeKey = `${hour.toString().padStart(2, '0')}:00:00`;
      return dayActions[timeKey]?.domesticHotWaterTemperature as ScheduleState | undefined;
    });
  };

  const todayStates = getStates(todayName);
  const tomorrowStates = getStates(tomorrowName);

  return (
    <div className="overflow-x-auto">
      <div className="grid min-w-[600px]" style={{ gridTemplateColumns: 'auto repeat(24, 1fr)', gap: '2px' }}>
        {/* Header */}
        <div />
        {Array.from({ length: 24 }, (_, i) => (
          <div key={i} className="text-center text-xs text-gray-500 dark:text-gray-400 py-1">{i}</div>
        ))}

        {/* Today */}
        <div className="text-sm font-medium pr-2 text-right py-1 whitespace-nowrap">Today</div>
        {todayStates.map((state, hour) => (
          <div
            key={`today-${hour}`}
            className={`h-8 rounded transition-transform hover:scale-105 ${state ? stateStyles[state] || 'bg-gray-200 dark:bg-gray-800' : 'bg-gray-200 dark:bg-gray-800'} ${hour === currentHour ? 'ring-2 ring-blue-400 ring-offset-1 dark:ring-offset-gray-950' : ''}`}
            title={`${hour}:00 - ${state || 'unset'}`}
          />
        ))}

        {/* Tomorrow */}
        <div className="text-sm font-medium pr-2 text-right py-1 whitespace-nowrap">Tomorrow</div>
        {tomorrowStates.map((state, hour) => (
          <div
            key={`tomorrow-${hour}`}
            className={`h-8 rounded transition-transform hover:scale-105 ${state ? stateStyles[state] || 'bg-gray-200 dark:bg-gray-800' : 'bg-gray-200 dark:bg-gray-800'}`}
            title={`${hour}:00 - ${state || 'unset'}`}
          />
        ))}
      </div>
    </div>
  );
}
