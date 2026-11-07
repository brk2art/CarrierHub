import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// `npm run dev`: the page on Vite's server, /v1 calls forwarded to the Ops Assistant (started by the AppHost).
// `npm run build`: the page goes into the Ops Assistant's wwwroot, which serves it: one address, no CORS.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/v1': { target: 'https://localhost:7243', changeOrigin: true, secure: false },
    },
  },
  build: {
    outDir: '../CarrierHub.OpsAssistant/wwwroot',
    emptyOutDir: true,
  },
})