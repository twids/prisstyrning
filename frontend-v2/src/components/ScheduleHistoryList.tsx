import { Disclosure, DisclosureButton, DisclosurePanel } from '@headlessui/react';
import { format } from 'date-fns';
import { useScheduleHistory } from '../hooks/useScheduleHistory';
import ScheduleGrid from './ScheduleGrid';

export default function ScheduleHistoryList() {
  const { data, isLoading, error } = useScheduleHistory();

  if (isLoading) {
    return (
      <div className="flex justify-center p-4">
        <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-blue-600" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 dark:bg-red-950 border border-red-200 dark:border-red-800 rounded-lg p-4 text-sm text-red-600 dark:text-red-400">
        Failed to load schedule history: {error.message}
      </div>
    );
  }

  if (!data || data.length === 0) {
    return (
      <div className="bg-blue-50 dark:bg-blue-950 border border-blue-200 dark:border-blue-800 rounded-lg p-4 text-sm text-blue-600 dark:text-blue-400">
        No schedule history found. Generate a schedule to see it here.
      </div>
    );
  }

  return (
    <div className="space-y-2">
      {data.map((entry, index) => {
        const timestamp = new Date(entry.timestamp);
        return (
          <Disclosure key={index}>
            {({ open }) => (
              <div className="border border-gray-200 dark:border-gray-800 rounded-xl overflow-hidden">
                <DisclosureButton className="w-full px-4 py-3 flex items-center justify-between text-left hover:bg-gray-50 dark:hover:bg-gray-800/50 transition-colors">
                  <div className="flex items-center gap-3">
                    <span className="text-sm">{format(timestamp, 'PPpp')}</span>
                    <span className="px-2 py-0.5 bg-gray-100 dark:bg-gray-800 rounded text-xs font-medium">{entry.date}</span>
                  </div>
                  <svg className={`w-4 h-4 text-gray-500 transition-transform ${open ? 'rotate-180' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M19 9l-7 7-7-7" />
                  </svg>
                </DisclosureButton>
                <DisclosurePanel className="px-4 pb-4">
                  <ScheduleGrid schedulePayload={entry.schedule} />
                </DisclosurePanel>
              </div>
            )}
          </Disclosure>
        );
      })}
    </div>
  );
}
