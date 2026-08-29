import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'dark',
    primary: {
      main: '#69d4c0',
    },
    secondary: {
      main: '#9dd7ff',
    },
    background: {
      default: '#071019',
      paper: '#0d1925',
    },
    success: {
      main: '#7bdca7',
    },
    warning: {
      main: '#f6c56f',
    },
    error: {
      main: '#ff8585',
    },
  },
  typography: {
    fontFamily: 'Inter, "Segoe UI", system-ui, sans-serif',
    h1: { fontWeight: 800 },
    h2: { fontWeight: 800 },
    h3: { fontWeight: 800 },
    h4: { fontWeight: 760 },
    button: { fontWeight: 700 },
  },
  components: {
    MuiButton: {
      styleOverrides: {
        root: {
          textTransform: 'none',
          borderRadius: 10,
        },
      },
    },
    MuiPaper: {
      styleOverrides: {
        root: {
          backgroundImage: 'none',
          borderColor: 'rgba(157, 215, 255, .14)',
        },
      },
    },
    MuiCssBaseline: {
      styleOverrides: {
        ':focus-visible': { outline: '3px solid #9dd7ff', outlineOffset: 2 },
        body: { backgroundImage: 'radial-gradient(circle at 78% -10%, rgba(105,212,192,.12), transparent 34%)' },
      },
    },
  },
});
