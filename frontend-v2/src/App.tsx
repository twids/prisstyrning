import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from './context/ThemeContext';
import { ToastProvider } from './context/ToastContext';
import Layout from './components/Layout';
import ErrorBoundary from './components/ErrorBoundary';
import DashboardPage from './pages/DashboardPage';
import SettingsPage from './pages/SettingsPage';
import AdminPage from './pages/AdminPage';
import NotFoundPage from './pages/NotFoundPage';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
    },
  },
});

function App() {
  return (
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <ThemeProvider>
          <ToastProvider>
            <BrowserRouter basename="/v2">
              <Layout>
                <Routes>
                  <Route path="/" element={<DashboardPage />} />
                  <Route path="/settings" element={<SettingsPage />} />
                  <Route path="/admin" element={<AdminPage />} />
                  <Route path="*" element={<NotFoundPage />} />
                </Routes>
              </Layout>
            </BrowserRouter>
          </ToastProvider>
        </ThemeProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}

export default App;
