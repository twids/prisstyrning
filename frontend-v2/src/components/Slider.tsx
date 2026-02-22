interface SliderProps {
  label: string;
  value: number;
  onChange: (value: number) => void;
  min: number;
  max: number;
  step: number;
  displayValue?: string;
  helpText?: string;
}

export default function Slider({ label, value, onChange, min, max, step, displayValue, helpText }: SliderProps) {
  return (
    <div>
      <div className="flex items-center justify-between mb-1">
        <label className="text-sm font-medium">{label}</label>
        <span className="text-sm text-gray-600 dark:text-gray-400">{displayValue ?? value}</span>
      </div>
      <input
        type="range"
        value={value}
        onChange={(e) => onChange(parseFloat(e.target.value))}
        min={min}
        max={max}
        step={step}
        className="w-full h-2 bg-gray-200 dark:bg-gray-700 rounded-lg appearance-none cursor-pointer accent-blue-600"
      />
      {helpText && <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">{helpText}</p>}
    </div>
  );
}
