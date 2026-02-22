import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  base: '/v2/',
  plugins: [react(), tailwindcss()],
  server: {
    port: 5174,
    proxy: {
      '/api': 'http://localhost:5000',
      '/auth': 'http://localhost:5000',
    },
  },
  build: {
    outDir: '../wwwroot-v2',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          query: ['@tanstack/react-query'],
          'date-utils': ['date-fns'],
          recharts: ['recharts'],
        },
      },
    },
  },
});
