import { useNavigate } from 'react-router-dom';

export default function NotFoundPage() {
  const navigate = useNavigate();
  return (
    <div className="text-center py-20">
      <h1 className="text-6xl font-bold text-gray-300 dark:text-gray-700 mb-4">404</h1>
      <h2 className="text-xl font-semibold mb-4">Page Not Found</h2>
      <button onClick={() => navigate('/')} className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 transition-colors">
        Go Home
      </button>
    </div>
  );
}
