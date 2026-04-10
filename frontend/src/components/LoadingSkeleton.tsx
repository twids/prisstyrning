export default function LoadingSkeleton() {
  return (
    <div className="space-y-4 animate-pulse">
      <div className="h-8 bg-muted rounded w-1/3" />
      <div className="h-48 bg-muted rounded" />
      <div className="h-48 bg-muted rounded" />
    </div>
  );
}
