export default function ScheduleLegend() {
  const items = [
    { color: 'bg-green-500', label: 'Comfort (Heating)' },
    { color: 'bg-blue-500', label: 'Eco (Daily DHW)' },
    { color: 'bg-red-500', label: 'Turn Off (No Heating)' },
  ];

  return (
    <div className="flex flex-wrap gap-4 mt-3">
      {items.map(({ color, label }) => (
        <div key={label} className="flex items-center gap-2">
          <span className={`w-3 h-3 rounded-full ${color}`} />
          <span className="text-sm text-gray-600 dark:text-gray-400">{label}</span>
        </div>
      ))}
    </div>
  );
}
