import { Component, ReactNode, ErrorInfo } from 'react';
import { Container, Paper, Typography, Button, Stack } from '@mui/material';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error?: Error;
}

export default class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('ErrorBoundary caught error:', error, errorInfo);
  }

  handleReset = () => {
    this.setState({ hasError: false, error: undefined });
    window.location.href = '/';
  };

  render() {
    if (this.state.hasError) {
      return (
        <Container maxWidth="sm" sx={{ py: 8 }}>
          <Paper sx={{ p: 4, textAlign: 'center' }}>
            <Stack spacing={3} alignItems="center">
              <ErrorOutlineIcon color="error" sx={{ fontSize: 64 }} />
              <Typography variant="h4">Something went wrong</Typography>
              <Typography variant="body1" color="text.secondary">
                {this.state.error?.message || 'An unexpected error occurred'}
              </Typography>
              <Button variant="contained" onClick={this.handleReset}>
                Return to Dashboard
              </Button>
            </Stack>
          </Paper>
        </Container>
      );
    }

    return this.props.children;
  }
}
