export default function LoadingSkeleton() {
  return (
    <div className="space-y-4">
      {[1, 2, 3].map(i => (
        <div key={i} className="bg-white dark:bg-gray-900 rounded-xl border border-gray-200 dark:border-gray-800 p-5 animate-pulse">
          <div className="h-5 bg-gray-200 dark:bg-gray-800 rounded w-1/3 mb-4" />
          <div className="h-32 bg-gray-200 dark:bg-gray-800 rounded" />
        </div>
      ))}
    </div>
  );
}
