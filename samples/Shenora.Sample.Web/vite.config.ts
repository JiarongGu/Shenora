import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Two apps' pages from one project: the Windows sample's (index.html, the default mode) and the Chromium sample's
// (chromium.html, `--mode chromium`). Each dev port must match its app's DevUrl, and is unique per app (the family
// rule: never 3000, so parallel dev sessions of sibling apps can't collide).
export default defineConfig(({ mode }) =>
  mode === 'chromium'
    ? {
        plugins: [react()],
        // samples/Shenora.Sample.Chromium/Program.cs: DevUrl, and the window's Path.
        server: { port: 3901, strictPort: true },
        build: {
          // The Chromium sample copies this output beside itself (gitignored artifact) for packaged mode.
          outDir: '../Shenora.Sample.Chromium/wwwroot',
          emptyOutDir: true,
          rollupOptions: { input: 'chromium.html' },
        },
      }
    : {
        plugins: [react()],
        // Must match the desktop sample's DevUrl (Program.cs).
        server: { port: 3900, strictPort: true },
        build: {
          // The desktop project embeds this output (gitignored artifact) for packaged mode.
          outDir: '../Shenora.Sample.Desktop/wwwroot',
          emptyOutDir: true,
        },
      },
);
