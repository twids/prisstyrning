interface JsonViewerProps {
  data: unknown;
  title?: string;
}

export default function JsonViewer({ data, title }: JsonViewerProps) {
  return (
    <div className="rounded-lg border bg-card p-4">
      {title && (
        <h4 className="text-sm font-medium mb-2">{title}</h4>
      )}
      <pre className="font-mono text-sm overflow-auto max-h-[400px] m-0 bg-muted/50 rounded p-3">
        {JSON.stringify(data, null, 2)}
      </pre>
    </div>
  );
}
