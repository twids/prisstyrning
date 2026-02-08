import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      '/auth': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    sourcemap: true,
    rollupOptions: {
      output: {
        manualChunks: {
          // Split MUI components into separate chunks
          'mui-core': ['@mui/material', '@mui/icons-material'],
          'mui-charts': ['@mui/x-charts'],
          // Split React and related libraries
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          // Split React Query
          'query': ['@tanstack/react-query'],
          // Split date utilities
          'date-utils': ['date-fns'],
        },
      },
    },
    chunkSizeWarningLimit: 600, // Raise limit to 600 KB (from default 500 KB)
  },
})
