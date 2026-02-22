interface JsonViewerProps {
  data: unknown;
  title?: string;
}

export default function JsonViewer({ data, title }: JsonViewerProps) {
  return (
    <div className="bg-gray-50 dark:bg-gray-950 rounded-lg border border-gray-200 dark:border-gray-800 p-4">
      {title && <h4 className="text-sm font-medium mb-2">{title}</h4>}
      <pre className="text-xs font-mono overflow-auto max-h-96 whitespace-pre-wrap">
        {JSON.stringify(data, null, 2)}
      </pre>
    </div>
  );
}
