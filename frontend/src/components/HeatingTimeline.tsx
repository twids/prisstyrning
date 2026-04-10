import type { SchedulePayload, ScheduleState } from '../types/api';

interface HeatingTimelineProps {
  schedulePayload: SchedulePayload | null;
  compact?: boolean;
}

type HourState = ScheduleState | undefined;

const DAY_NAMES: Record<number, string> = {
  0: 'sunday',
  1: 'monday',
  2: 'tuesday',
  3: 'wednesday',
  4: 'thursday',
  5: 'friday',
  6: 'saturday',
};

function stateColor(state: HourState): string {
  switch (state) {
    case 'comfort':
      return 'hsl(var(--accent))';
    case 'eco':
      return 'hsl(var(--primary))';
    case 'turn_off':
      return 'hsl(var(--destructive))';
    default:
      return 'hsl(var(--muted))';
  }
}

function stateLabel(state: HourState): string {
  switch (state) {
    case 'comfort':
      return 'Komfort';
    case 'eco':
      return 'Eco';
    case 'turn_off':
      return 'Av';
    default:
      return 'Odefinierad';
  }
}

function extractDayStates(
  schedulePayload: SchedulePayload,
  dayName: string
): HourState[] {
  const keys = Object.keys(schedulePayload);
  if (keys.length === 0) {
    return Array(24).fill(undefined);
  }
  const scheduleId = keys[0];
  const schedule = schedulePayload[scheduleId];
  const dayActions = schedule?.actions?.[dayName] ?? {};

  const states: HourState[] = Array(24).fill(undefined);

  // Fill hours from time keys
  for (const [timeKey, action] of Object.entries(dayActions)) {
    const [hourStr] = timeKey.split(':');
    const hour = parseInt(hourStr, 10);
    if (hour >= 0 && hour < 24) {
      states[hour] = action.domesticHotWaterTemperature as HourState;
    }
  }

  return states;
}

interface DayRowProps {
  label: string;
  states: HourState[];
  currentHour?: number;
  compact: boolean;
}

function DayRow({ label, states, currentHour, compact }: DayRowProps) {
  return (
    <div className="flex items-center gap-2">
      <span className={`text-muted-foreground shrink-0 text-right ${compact ? 'text-xs w-12' : 'text-sm w-16'}`}>
        {label}
      </span>
      <div className="flex flex-1 gap-px">
        {states.map((state, hour) => {
          const isCurrent = currentHour === hour;
          return (
            <div
              key={hour}
              title={`${String(hour).padStart(2, '0')}:00 - ${stateLabel(state)}`}
              className={`flex-1 rounded-sm transition-transform hover:scale-110 cursor-default ${
                compact ? 'h-6' : 'h-12'
              } ${isCurrent ? 'ring-2 ring-offset-1 ring-ring' : ''}`}
              style={{ backgroundColor: stateColor(state) }}
            />
          );
        })}
      </div>
    </div>
  );
}

export default function HeatingTimeline({ schedulePayload, compact = false }: HeatingTimelineProps) {
  const now = new Date();
  const currentHour = now.getHours();
  const todayIndex = now.getDay();
  const tomorrowIndex = (todayIndex + 1) % 7;

  const todayName = DAY_NAMES[todayIndex];
  const tomorrowName = DAY_NAMES[tomorrowIndex];

  if (!schedulePayload) {
    return (
      <p className="text-sm text-muted-foreground">Inget schema tillgängligt</p>
    );
  }

  const todayStates = extractDayStates(schedulePayload, todayName);
  const tomorrowStates = extractDayStates(schedulePayload, tomorrowName);

  return (
    <div className="space-y-2">
      {/* Hour labels */}
      {!compact && (
        <div className="flex items-center gap-2">
          <span className="w-16 shrink-0" />
          <div className="flex flex-1 gap-px">
            {Array.from({ length: 24 }, (_, h) => (
              <div key={h} className="flex-1 text-center text-[9px] text-muted-foreground">
                {h % 4 === 0 ? String(h).padStart(2, '0') : ''}
              </div>
            ))}
          </div>
        </div>
      )}

      <DayRow
        label="Idag"
        states={todayStates}
        currentHour={currentHour}
        compact={compact}
      />
      <DayRow
        label="Imorgon"
        states={tomorrowStates}
        compact={compact}
      />

      {/* Legend */}
      {!compact && (
        <div className="flex items-center gap-4 mt-2 pt-2 border-t border-border">
          {(['comfort', 'eco', 'turn_off', undefined] as HourState[]).map((s) => (
            <div key={String(s)} className="flex items-center gap-1.5">
              <div
                className="w-3 h-3 rounded-sm shrink-0"
                style={{ backgroundColor: stateColor(s) }}
              />
              <span className="text-xs text-muted-foreground">{stateLabel(s)}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
