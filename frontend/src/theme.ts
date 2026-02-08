import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'dark', // Match current UI
    primary: {
      main: '#4FC3F7', // Light blue (current chart color)
    },
    secondary: {
      main: '#FFB74D', // Orange (current chart color)
    },
    background: {
      default: '#121212',
      paper: '#1e1e1e',
    },
    success: {
      main: '#8BC34A', // Green for comfort mode
    },
    warning: {
      main: '#FFB74D', // Orange for eco mode (legacy)
    },
    error: {
      main: '#EF5350', // Red for turn_off mode
    },
  },
  typography: {
    fontFamily: '"Roboto", "Helvetica", "Arial", sans-serif',
  },
  components: {
    MuiButton: {
      styleOverrides: {
        root: {
          textTransform: 'none', // No ALL CAPS
        },
      },
    },
  },
});
