import { Injectable, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';

/**
 * The broad product areas the canonical feedback page accepts as an origin
 * hint. This is the whole `?from=` vocabulary the app may emit, so the link can
 * never carry a route parameter, a title, an id or any other user content.
 */
export type FeedbackOrigin =
  | 'library'
  | 'reader'
  | 'brain'
  | 'studio'
  | 'settings';

/**
 * Maps the current route to the broad product area it belongs to. Returns null
 * for routes outside the product shell; the capability URL's own default origin
 * then stands unchanged.
 */
export function feedbackOriginForUrl(url: string): FeedbackOrigin | null {
  const path = url.split(/[?#]/, 1)[0];
  if (path === '/library' || path.startsWith('/library/')) return 'library';
  if (path.startsWith('/read/')) return 'reader';
  if (path === '/second-brain') return 'brain';
  if (path === '/studio') return 'studio';
  if (path === '/settings') return 'settings';
  return null;
}

/** Replaces the capability URL's origin hint with the current product area. */
export function withFeedbackOrigin(url: string, origin: FeedbackOrigin): string {
  try {
    const parsed = new URL(url);
    parsed.searchParams.set('from', origin);
    return parsed.toString();
  } catch {
    return url;
  }
}

/**
 * The one place that builds the app's feedback destination.
 *
 * The server capability URL is authoritative for whether a feedback surface
 * exists at all (Cloud) and for the canonical page; this service only rewrites
 * its harmless `from=` hint to the current broad product area. Settings no
 * longer carries its own copy of the link.
 */
@Injectable({ providedIn: 'root' })
export class FeedbackLinkService {
  private readonly router = inject(Router);
  private readonly deploymentCapabilities = inject(DeploymentCapabilitiesService);

  private readonly currentUrl = signal(this.router.url);
  private readonly capabilities = signal<DeploymentCapabilities | null>(null);

  /** The Cloud feedback destination for the current area, or null otherwise. */
  readonly url = computed(() => {
    const capabilities = this.capabilities();
    if (capabilities?.deploymentMode !== 'Cloud' || !capabilities.feedbackUrl) return null;

    const origin = feedbackOriginForUrl(this.currentUrl());
    return origin
      ? withFeedbackOrigin(capabilities.feedbackUrl, origin)
      : capabilities.feedbackUrl;
  });

  constructor() {
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.currentUrl.set(this.router.url));

    this.deploymentCapabilities.get().subscribe({
      next: (capabilities) => this.capabilities.set(capabilities),
      error: () => this.capabilities.set(null),
    });
  }
}
