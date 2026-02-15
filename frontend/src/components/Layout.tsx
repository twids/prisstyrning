import { ReactNode } from 'react';
import { AppBar, Toolbar, Typography, Container, Button, Box } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import SettingsIcon from '@mui/icons-material/Settings';
import DashboardIcon from '@mui/icons-material/Dashboard';
import AdminPanelSettingsIcon from '@mui/icons-material/AdminPanelSettings';

interface LayoutProps {
  children: ReactNode;
}

export default function Layout({ children }: LayoutProps) {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
      <AppBar position="static">
        <Toolbar>
          <Typography variant="h6" component="div" sx={{ flexGrow: 1 }}>
            Prisstyrning
          </Typography>
          <Button
            color="inherit"
            component={RouterLink}
            to="/"
            startIcon={<DashboardIcon />}
          >
            Dashboard
          </Button>
          <Button
            color="inherit"
            component={RouterLink}
            to="/settings"
            startIcon={<SettingsIcon />}
          >
            Settings
          </Button>
          <Button
            color="inherit"
            component={RouterLink}
            to="/admin"
            startIcon={<AdminPanelSettingsIcon />}
          >
            Admin
          </Button>
        </Toolbar>
      </AppBar>
      <Container component="main" sx={{ flex: 1, py: 4 }}>
        {children}
      </Container>
    </Box>
  );
}
