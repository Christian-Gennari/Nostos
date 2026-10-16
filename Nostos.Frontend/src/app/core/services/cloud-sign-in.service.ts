import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { CloudSignInOptions } from '../dtos/cloud-auth.dtos';

/**
 * The slice of the Clerk browser SDK the Cloud sign-in screen uses. Kept
 * local so the app does not depend on a Clerk package: the script is served
 * by the instance's own Frontend API and only ever loaded on Cloud.
 */
export interface ClerkSignInHost {
  readonly user: { primaryEmailAddress?: { emailAddress: string } | null } | null | undefined;
  load(options?: Record<string, unknown>): Promise<void>;
  mountSignIn(node: HTMLElement, props?: Record<string, unknown>): void;
  unmountSignIn(node: HTMLElement): void;
  signOut(): Promise<void>;
}

/** Clerk v5 ships its components in the one browser bundle. */
const CLERK_JS_MAJOR = 5;
const LOAD_TIMEOUT_MS = 15_000;

/**
 * Cloud sign-in on the Nostos origin.
 *
 * The backend stays the session authority: the embedded form only creates
 * the identity-provider session, and the browser then makes the usual
 * top-level trip through `/api/auth/login`, which completes the OIDC code
 * flow without showing anything and sets the HttpOnly Nostos cookie. No
 * provider token is read or kept here.
 *
 * Every failure (no options endpoint, a key that is not a Clerk key, a
 * script that does not load) resolves to `null`, and the caller falls back
 * to the provider-hosted sign-in page.
 */
@Injectable({ providedIn: 'root' })
export class CloudSignInService {
  private readonly http = inject(HttpClient);
  private clerk?: Promise<ClerkSignInHost | null>;

  async options(): Promise<CloudSignInOptions | null> {
    try {
      const options = await firstValueFrom(
        this.http.get<CloudSignInOptions>('/api/auth/sign-in-options'),
      );
      return options?.provider === 'clerk' && frontendApiFromPublishableKey(options.publishableKey)
        ? options
        : null;
    } catch {
      return null;
    }
  }

  loadClerk(publishableKey: string): Promise<ClerkSignInHost | null> {
    this.clerk ??= this.load(publishableKey).then((clerk) => {
      if (!clerk) this.clerk = undefined;
      return clerk;
    });
    return this.clerk;
  }

  private async load(publishableKey: string): Promise<ClerkSignInHost | null> {
    const frontendApi = frontendApiFromPublishableKey(publishableKey);
    if (!frontendApi) return null;

    const win = globalThis as unknown as { Clerk?: ClerkSignInHost };
    try {
      if (!win.Clerk) {
        await withTimeout(injectScript(frontendApi, publishableKey), LOAD_TIMEOUT_MS);
      }
      const clerk = win.Clerk;
      if (!clerk) return null;
      await withTimeout(clerk.load(), LOAD_TIMEOUT_MS);
      return clerk;
    } catch {
      return null;
    }
  }
}

/**
 * A Clerk publishable key is `pk_live_` / `pk_test_` followed by the
 * base64 of the Frontend API host and a trailing `$`. Only a plain host
 * name is accepted, since it becomes the origin of a script tag.
 */
export function frontendApiFromPublishableKey(key: string | null | undefined): string | null {
  const match = /^pk_(?:live|test)_([A-Za-z0-9+/=]+)$/.exec(key ?? '');
  if (!match) return null;
  let decoded: string;
  try {
    decoded = atob(match[1]);
  } catch {
    return null;
  }
  if (!decoded.endsWith('$')) return null;
  const host = decoded.slice(0, -1);
  return /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+$/i.test(host)
    ? host
    : null;
}

function injectScript(frontendApi: string, publishableKey: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.async = true;
    script.crossOrigin = 'anonymous';
    script.src = `https://${frontendApi}/npm/@clerk/clerk-js@${CLERK_JS_MAJOR}/dist/clerk.browser.js`;
    script.setAttribute('data-clerk-publishable-key', publishableKey);
    script.onload = () => resolve();
    script.onerror = () => {
      script.remove();
      reject(new Error('Clerk script failed to load'));
    };
    document.head.appendChild(script);
  });
}

function withTimeout<T>(promise: Promise<T>, ms: number): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const handle = setTimeout(() => reject(new Error('timed out')), ms);
    promise.then(
      (value) => {
        clearTimeout(handle);
        resolve(value);
      },
      (error) => {
        clearTimeout(handle);
        reject(error);
      },
    );
  });
}
