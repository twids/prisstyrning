import { Box, Typography, Paper } from '@mui/material';

interface JsonViewerProps {
  data: unknown;
  title?: string;
}

export default function JsonViewer({ data, title }: JsonViewerProps) {
  return (
    <Paper sx={{ p: 2, bgcolor: 'background.default' }}>
      {title && (
        <Typography variant="subtitle2" gutterBottom>
          {title}
        </Typography>
      )}
      <Box
        component="pre"
        sx={{
          fontFamily: 'monospace',
          fontSize: '0.875rem',
          overflow: 'auto',
          maxHeight: 400,
          margin: 0,
        }}
      >
        {JSON.stringify(data, null, 2)}
      </Box>
    </Paper>
  );
}
