import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The SPA and the API ship in one container, so the production build lands
// directly in the API's wwwroot and is served same-origin. That removes CORS
// from production entirely and leaves one thing to deploy instead of two.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../ShiftReason.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5217', changeOrigin: true },
      // ws:true matters — without it SignalR silently falls back to long
      // polling in development and the live grid looks laggy for no reason.
      '/hubs': { target: 'http://localhost:5217', changeOrigin: true, ws: true },
    },
  },
})
