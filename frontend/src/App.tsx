import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider, CssBaseline } from '@mui/material';
import { theme } from './theme';
import Layout from './components/Layout';
import DashboardPage from './pages/DashboardPage';
import SettingsPage from './pages/SettingsPage';
import AdminPage from './pages/AdminPage';
import NotFoundPage from './pages/NotFoundPage';
import ThermalOverviewPage from './pages/thermal/ThermalOverviewPage';
import ThermalPlanPage from './pages/thermal/ThermalPlanPage';
import ThermalRoomsPage from './pages/thermal/ThermalRoomsPage';
import ThermalDhwPage from './pages/thermal/ThermalDhwPage';
import ThermalModelPage from './pages/thermal/ThermalModelPage';
import ThermalEventsPage from './pages/thermal/ThermalEventsPage';
import ThermalSettingsPage from './pages/thermal/ThermalSettingsPage';
import ErrorBoundary from './components/ErrorBoundary';
import { TimezoneProvider } from './context/TimezoneContext';

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
        <ThemeProvider theme={theme}>
          <CssBaseline />
          <TimezoneProvider>
            <BrowserRouter key="router">
              <Layout>
                <Routes>
                  <Route path="/" element={<ThermalOverviewPage />} />
                  <Route path="/plan" element={<ThermalPlanPage />} />
                  <Route path="/rooms" element={<ThermalRoomsPage />} />
                  <Route path="/dhw" element={<ThermalDhwPage />} />
                  <Route path="/model" element={<ThermalModelPage />} />
                  <Route path="/events" element={<ThermalEventsPage />} />
                  <Route path="/settings" element={<ThermalSettingsPage />} />
                  <Route path="/legacy" element={<DashboardPage />} />
                  <Route path="/legacy/settings" element={<SettingsPage />} />
                  <Route path="/admin" element={<AdminPage />} />
                  <Route path="*" element={<NotFoundPage />} />
                </Routes>
              </Layout>
            </BrowserRouter>
          </TimezoneProvider>
        </ThemeProvider>
      </QueryClientProvider>
    </ErrorBoundary>
  );
}

export default App;
