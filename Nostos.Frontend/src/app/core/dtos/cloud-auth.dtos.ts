export type CloudAccountState = 'Unknown' | 'Active' | 'Disabled' | 'Deleted' | 'DeletionRequested';

export interface CloudSessionAccount {
  id: string;
  displayName: string;
  email: string | null;
}

export interface CloudSession {
  authenticated: boolean;
  accountState: CloudAccountState | null;
  account: CloudSessionAccount | null;
}

/**
 * Cloud-only answer from `GET /api/auth/sign-in-options`: what the browser
 * needs to show the identity provider's sign-in form on the Nostos origin.
 * The publishable key is public by design.
 */
export interface CloudSignInOptions {
  provider: 'clerk';
  publishableKey: string;
}
