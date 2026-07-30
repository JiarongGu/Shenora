import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { installDevInterceptor } from '@shenora/react';
import { App } from './App';

// Dev-only: window.__shenora (call/waitEvent/ring buffers) for the CDP-driven e2e loop.
if (import.meta.env.DEV) installDevInterceptor();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
