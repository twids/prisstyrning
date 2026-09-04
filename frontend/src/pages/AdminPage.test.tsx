import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from '@mui/material';
import { axe } from 'vitest-axe';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AdminUser } from '../types/api';
import { theme } from '../theme';
import AdminPage from './AdminPage';

const requests = vi.hoisted(() => ({
  getAdminStatus: vi.fn(), getAdminUsers: vi.fn(), adminLogin: vi.fn(),
  grantAdmin: vi.fn(), revokeAdmin: vi.fn(), grantHangfire: vi.fn(), revokeHangfire: vi.fn(), deleteUser: vi.fn(),
}));
vi.mock('../api/client', () => ({ apiClient: requests }));

const administrator: AdminUser = {
  userId: 'test-admin', zone: 'SE3', settings: { ComfortHours: 3, TurnOffPercentile: .9, MaxComfortGapHours: 28 },
  daikinAuthorized: true, daikinExpiresAtUtc: null, daikinSubject: null, hasScheduleHistory: true,
  scheduleCount: 5, lastScheduleDate: null, isAdmin: true, isCurrentUser: true, hasHangfireAccess: false, createdAt: null,
};
const other: AdminUser = { ...administrator, userId: 'test-other', isAdmin: false, isCurrentUser: false, daikinAuthorized: false };

function renderAdmin() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity }, mutations: { retry: false } },
  });
  return {
    ...render(<QueryClientProvider client={queryClient}><ThemeProvider theme={theme}><main><AdminPage /></main></ThemeProvider></QueryClientProvider>),
    queryClient,
  };
}

describe('AdminPage säker kontolivscykel', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    requests.getAdminStatus.mockResolvedValue({ isAdmin: true, userId: administrator.userId });
    requests.getAdminUsers.mockResolvedValue({ users: [administrator, other] });
    requests.grantAdmin.mockResolvedValue({ granted: true, userId: other.userId });
    requests.grantHangfire.mockResolvedValue({ granted: true, userId: other.userId });
  });

  it('förklarar spärren och erbjuder ingen partiell radering, heller inte för äldre konton', async () => {
    renderAdmin();
    await screen.findByRole('table', { name: 'Användare och behörigheter' });

    expect(screen.getByRole('note')).toHaveTextContent('Kontoradering är tillfälligt spärrad');
    expect(screen.getByRole('note')).toHaveTextContent('befintlig schemastyrning fortsätter');
    const buttons = screen.getAllByRole('button', { name: /^Radering spärrad för/ });
    expect(buttons).toHaveLength(2);
    for (const button of buttons) {
      expect(button).toBeDisabled();
      expect(button).toHaveAccessibleDescription(/Säker kontoradering måste hantera pågående styrning/);
      button.click();
    }
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByText(/kommer att raderas permanent/)).not.toBeInTheDocument();
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('behåller separat behörighetshantering utan att öppna raderingen', async () => {
    const user = userEvent.setup();
    renderAdmin();
    const ownAdmin = await screen.findByRole('switch', { name: 'Adminbehörighet för test-admin' });
    expect(ownAdmin).toBeDisabled();
    await user.click(screen.getByRole('switch', { name: 'Adminbehörighet för test-other' }));

    await waitFor(() => expect(requests.grantAdmin).toHaveBeenCalledWith('test-other'));
    expect(requests.revokeAdmin).not.toHaveBeenCalled();
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('namnger även Hangfire-behörigheten och skiljer den från radering', async () => {
    const user = userEvent.setup();
    renderAdmin();
    await user.click(await screen.findByRole('switch', { name: 'Hangfire-behörighet för test-other' }));

    await waitFor(() => expect(requests.grantHangfire).toHaveBeenCalledWith('test-other'));
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('visar en säker återförsökssida när adminbehörigheten inte kan verifieras', async () => {
    const user = userEvent.setup();
    requests.getAdminStatus.mockRejectedValueOnce(new Error('private-proxy-detail'));
    renderAdmin();

    expect(await screen.findByRole('alert')).toHaveTextContent('Adminbehörigheten kunde inte kontrolleras');
    expect(screen.queryByLabelText('Lösenord')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText(/private-proxy-detail/)).not.toBeInTheDocument();
    expect(requests.getAdminUsers).not.toHaveBeenCalled();

    await user.click(screen.getByRole('button', { name: 'Försök igen' }));
    await screen.findByRole('table');
    expect(requests.getAdminStatus).toHaveBeenCalledTimes(2);
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('döljer cachade konton och åtgärder om en senare behörighetskontroll misslyckas', async () => {
    const { queryClient } = renderAdmin();
    await screen.findByRole('table');
    requests.getAdminStatus.mockRejectedValue(new Error('private-auth-detail'));

    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['admin-status'] }); });

    expect(await screen.findByRole('alert')).toHaveTextContent('Adminbehörigheten kunde inte kontrolleras');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText('test-other')).not.toBeInTheDocument();
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.queryByText(/private-auth-detail/)).not.toBeInTheDocument();
  });

  it('döljer cachad användarlista vid hämtningsfel och visar den först efter lyckat återförsök', async () => {
    const user = userEvent.setup();
    const { queryClient } = renderAdmin();
    await screen.findByRole('table');
    requests.getAdminUsers.mockRejectedValueOnce(new Error('private-user-detail'));

    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['admin-users'] }); });

    expect(await screen.findByRole('alert')).toHaveTextContent('Användarlistan kunde inte hämtas');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText('test-other')).not.toBeInTheDocument();
    expect(screen.queryByText(/private-user-detail/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Försök igen' }));
    await screen.findByRole('table');
    expect(screen.getByRole('button', { name: 'Radering spärrad för test-other' })).toBeDisabled();
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('visar inte råa fel eller falsk framgång efter en misslyckad behörighetsändring', async () => {
    const user = userEvent.setup();
    requests.grantAdmin.mockRejectedValueOnce(new Error('private-mutation-detail'));
    renderAdmin();
    await user.click(await screen.findByRole('switch', { name: 'Adminbehörighet för test-other' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Ändringen av adminbehörighet kunde inte bekräftas');
    expect(screen.queryByText(/private-mutation-detail/)).not.toBeInTheDocument();
    expect(requests.deleteUser).not.toHaveBeenCalled();
  });

  it('återger admininloggningsfel på svenska utan serverdetaljer', async () => {
    const user = userEvent.setup();
    requests.getAdminStatus.mockResolvedValue({ isAdmin: false, userId: other.userId });
    requests.adminLogin.mockRejectedValueOnce(new Error('private-login-detail'));
    renderAdmin();
    await user.type(await screen.findByLabelText('Lösenord'), 'synthetic-password');
    await user.click(screen.getByRole('button', { name: 'Logga in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Administratörsinloggningen misslyckades');
    expect(screen.queryByText(/private-login-detail/)).not.toBeInTheDocument();
    expect(requests.getAdminUsers).not.toHaveBeenCalled();
  });

  it('ger rullningsytan tangentbordsfokus och klarar automatiska tillgänglighetskontroller', async () => {
    const { container } = renderAdmin();
    await screen.findByRole('table');
    const list = screen.getByRole('region', { name: 'Användarlista' });
    list.focus();
    expect(list).toHaveFocus();
    expect(screen.getByRole('img', { name: 'Daikin-auktorisering finns' })).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Daikin-auktorisering saknas' })).toBeInTheDocument();
    expect((await axe(container, { rules: { 'color-contrast': { enabled: false } } })).violations).toHaveLength(0);
  });
});
