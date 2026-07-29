import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // Must match the desktop sample's DevUrl (Program.cs). Unique per app — the family rule:
    // never 3000, so parallel dev sessions of sibling apps can't collide.
    port: 3900,
    strictPort: true,
  },
  build: {
    // The desktop project embeds this output (gitignored artifact) for packaged mode.
    outDir: '../Shenora.Sample.Desktop/wwwroot',
    emptyOutDir: true,
  },
});
