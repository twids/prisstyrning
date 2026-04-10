import { useNavigate } from 'react-router-dom';

export default function NotFoundPage() {
  const navigate = useNavigate();
  
  return (
    <div className="text-center mt-16 space-y-4">
      <h1 className="text-6xl font-bold">404</h1>
      <h2 className="text-xl text-muted-foreground">Page Not Found</h2>
      <button
        onClick={() => navigate('/')}
        className="inline-flex items-center justify-center rounded-md bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90"
      >
        Go Home
      </button>
    </div>
  );
}
