/** Lifecycle hints only. The host resolves provider identity server-side. */
export interface HostedBrowserSession {
  authenticated: boolean;
  accountId: string | null;
}

/** Installed by the optional same-origin host script before its load event. */
export interface HostedBrowserIntegration {
  sessionChanged(session: HostedBrowserSession): void;
}

declare global {
  interface Window {
    nostosHostedBrowserIntegration?: HostedBrowserIntegration;
  }
}
