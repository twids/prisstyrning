import type { ReactNode } from 'react';
import {
  AppBar, Box, Button, Container, Divider, List, ListItemButton, ListItemIcon,
  ListItemText, Stack, Toolbar, Tooltip, Typography,
} from '@mui/material';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import DashboardOutlinedIcon from '@mui/icons-material/DashboardOutlined';
import TimelineOutlinedIcon from '@mui/icons-material/TimelineOutlined';
import MeetingRoomOutlinedIcon from '@mui/icons-material/MeetingRoomOutlined';
import WaterDropOutlinedIcon from '@mui/icons-material/WaterDropOutlined';
import ModelTrainingOutlinedIcon from '@mui/icons-material/ModelTrainingOutlined';
import ReceiptLongOutlinedIcon from '@mui/icons-material/ReceiptLongOutlined';
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined';
import HistoryOutlinedIcon from '@mui/icons-material/HistoryOutlined';
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined';
import HeatPumpOutlinedIcon from '@mui/icons-material/HeatPumpOutlined';
import LogoutOutlinedIcon from '@mui/icons-material/LogoutOutlined';
import { apiClient } from '../api/client';
import ThermalStatusStrip from './thermal/ThermalStatusStrip';
import { useLogout } from '../hooks/useSession';

interface LayoutProps { children: ReactNode }

const thermalNavigation = [
  { label: 'Översikt', path: '/', icon: <DashboardOutlinedIcon /> },
  { label: 'Plan', path: '/plan', icon: <TimelineOutlinedIcon /> },
  { label: 'Rum', path: '/rooms', icon: <MeetingRoomOutlinedIcon /> },
  { label: 'Varmvatten', path: '/dhw', icon: <WaterDropOutlinedIcon /> },
  { label: 'Modell', path: '/model', icon: <ModelTrainingOutlinedIcon /> },
  { label: 'Händelser', path: '/events', icon: <ReceiptLongOutlinedIcon /> },
  { label: 'Inställningar', path: '/settings', icon: <SettingsOutlinedIcon /> },
];

export default function Layout({ children }: LayoutProps) {
  const location = useLocation();
  const adminStatusQuery = useQuery({ queryKey: ['admin-status'], queryFn: () => apiClient.getAdminStatus(), staleTime: 5 * 60 * 1000 });
  const isAdmin = adminStatusQuery.data?.isAdmin ?? false;
  const logout = useLogout();

  const navItem = (item: typeof thermalNavigation[number]) => {
    const selected = item.path === '/' ? location.pathname === '/' : location.pathname.startsWith(item.path);
    return (
      <ListItemButton key={item.path} component={RouterLink} to={item.path} selected={selected} sx={{ borderRadius: 2, mb: .4 }}>
        <ListItemIcon sx={{ minWidth: 38 }}>{item.icon}</ListItemIcon>
        <ListItemText primary={item.label} primaryTypographyProps={{ fontWeight: selected ? 750 : 520 }} />
      </ListItemButton>
    );
  };

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="sticky" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider', bgcolor: 'rgba(7, 15, 25, .94)', backdropFilter: 'blur(16px)' }}>
        <Toolbar sx={{ minHeight: { xs: 60, md: 68 } }}>
          <Stack direction="row" alignItems="center" spacing={1.3} sx={{ flexGrow: 1 }}>
            <Box sx={{ width: 36, height: 36, borderRadius: 2, display: 'grid', placeItems: 'center', color: '#071019', background: 'linear-gradient(135deg, #69d4c0, #9dd7ff)' }}><HeatPumpOutlinedIcon /></Box>
            <Box>
              <Typography variant="h6" lineHeight={1.05} fontWeight={800}>Prisstyrning</Typography>
              <Typography variant="caption" color="text.secondary">Värmeorkestrator</Typography>
            </Box>
          </Stack>
          <Button component={RouterLink} to="/legacy" color="inherit" startIcon={<HistoryOutlinedIcon />} sx={{ display: { xs: 'none', sm: 'inline-flex' } }}>Legacy-vy</Button>
          <Tooltip title="Logga ut från Prisstyrning"><Button color="inherit" startIcon={<LogoutOutlinedIcon />} onClick={() => logout.mutate()} disabled={logout.isPending}>{logout.isPending ? 'Loggar ut…' : 'Logga ut'}</Button></Tooltip>
        </Toolbar>
        <Box component="nav" aria-label="Huvudnavigation" sx={{ display: { xs: 'flex', lg: 'none' }, overflowX: 'auto', px: 1, pb: 1, gap: .5, scrollbarWidth: 'none', '&::-webkit-scrollbar': { display: 'none' } }}>
          {thermalNavigation.map((item) => (
            <Button key={item.path} component={RouterLink} to={item.path} color={location.pathname === item.path ? 'primary' : 'inherit'} startIcon={item.icon} sx={{ whiteSpace: 'nowrap' }}>{item.label}</Button>
          ))}
        </Box>
      </AppBar>
      <ThermalStatusStrip />
      <Box sx={{ display: 'flex' }}>
        <Box component="nav" aria-label="Huvudnavigation" sx={{ display: { xs: 'none', lg: 'block' }, width: 238, flexShrink: 0, borderRight: 1, borderColor: 'divider', minHeight: 'calc(100vh - 110px)', p: 1.5 }}>
          <Typography variant="overline" color="text.secondary" sx={{ px: 1.5 }}>Ny styrning</Typography>
          <List dense>{thermalNavigation.map(navItem)}</List>
          <Divider sx={{ my: 1.5 }} />
          <Typography variant="overline" color="text.secondary" sx={{ px: 1.5 }}>Befintlig funktion</Typography>
          <List dense>
            <ListItemButton component={RouterLink} to="/legacy" selected={location.pathname === '/legacy'} sx={{ borderRadius: 2 }}>
              <ListItemIcon sx={{ minWidth: 38 }}><HistoryOutlinedIcon /></ListItemIcon><ListItemText primary="Legacy-DHW" />
            </ListItemButton>
            <ListItemButton component={RouterLink} to="/legacy/settings" selected={location.pathname === '/legacy/settings'} sx={{ borderRadius: 2 }}>
              <ListItemIcon sx={{ minWidth: 38 }}><SettingsOutlinedIcon /></ListItemIcon><ListItemText primary="Legacy-inställningar" />
            </ListItemButton>
            {isAdmin && <ListItemButton component={RouterLink} to="/admin" selected={location.pathname === '/admin'} sx={{ borderRadius: 2 }}><ListItemIcon sx={{ minWidth: 38 }}><AdminPanelSettingsOutlinedIcon /></ListItemIcon><ListItemText primary="Admin" /></ListItemButton>}
          </List>
        </Box>
        <Container component="main" maxWidth="xl" sx={{ flex: 1, minWidth: 0, py: { xs: 3, md: 5 }, px: { xs: 2, md: 4 } }}>
          {children}
        </Container>
      </Box>
    </Box>
  );
}
