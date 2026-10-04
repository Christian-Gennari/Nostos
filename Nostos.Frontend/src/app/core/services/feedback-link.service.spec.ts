import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import {
  FeedbackLinkService,
  feedbackOriginForUrl,
  withFeedbackOrigin,
} from './feedback-link.service';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { DeploymentCapabilities } from '../dtos/deployment-capabilities.dtos';

@Component({ standalone: true, template: '' })
class BlankComponent {}

const cloudCapabilities: DeploymentCapabilities = {
  deploymentMode: 'Cloud',
  requiresAuthentication: true,
  canConfigureAiProvider: false,
  managedAi: true,
  managedVoiceTranscription: true,
  usesCloudStorage: true,
  supportsLocalBackupConfiguration: false,
  supportsPrivateNetworkAccess: false,
  supportsEreaderAccess: true,
  usageMeteringAvailable: true,
  accountManagementUrl: 'https://nostos.page/account',
  feedbackUrl: 'https://nostos.page/feedback?from=settings',
};

const selfHostedCapabilities: DeploymentCapabilities = {
  ...cloudCapabilities,
  deploymentMode: 'SelfHosted',
  requiresAuthentication: false,
  managedAi: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  usageMeteringAvailable: false,
  accountManagementUrl: null,
  feedbackUrl: null,
};

describe('feedbackOriginForUrl', () => {
  it('maps each shell surface to its broad product area', () => {
    expect(feedbackOriginForUrl('/library')).toBe('library');
    expect(feedbackOriginForUrl('/library/2f4b')).toBe('library');
    expect(feedbackOriginForUrl('/library/2f4b?tab=notes')).toBe('library');
    expect(feedbackOriginForUrl('/read/2f4b')).toBe('reader');
    expect(feedbackOriginForUrl('/second-brain')).toBe('brain');
    expect(feedbackOriginForUrl('/second-brain?topicId=abc')).toBe('brain');
    expect(feedbackOriginForUrl('/studio')).toBe('studio');
    expect(feedbackOriginForUrl('/settings')).toBe('settings');
  });

  it('returns null for routes with no broad area, so the default origin stands', () => {
    expect(feedbackOriginForUrl('/ui-catalogue')).toBeNull();
    expect(feedbackOriginForUrl('/')).toBeNull();
  });
});

describe('withFeedbackOrigin', () => {
  it('replaces the capability URL default origin', () => {
    expect(withFeedbackOrigin('https://nostos.page/feedback?from=settings', 'library')).toBe(
      'https://nostos.page/feedback?from=library',
    );
  });

  it('preserves unrelated query parameters', () => {
    expect(
      withFeedbackOrigin('https://nostos.page/feedback?from=settings&lang=en', 'brain'),
    ).toBe('https://nostos.page/feedback?from=brain&lang=en');
  });

  it('leaves a malformed capability URL untouched', () => {
    expect(withFeedbackOrigin('not a url', 'studio')).toBe('not a url');
  });
});

describe('FeedbackLinkService', () => {
  let capabilities: DeploymentCapabilities;
  let capabilitiesError: Error | null;

  beforeEach(() => {
    capabilities = cloudCapabilities;
    capabilitiesError = null;

    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: BlankComponent }]),
        {
          provide: DeploymentCapabilitiesService,
          useValue: {
            get: () =>
              capabilitiesError ? throwError(() => capabilitiesError) : of(capabilities),
          },
        },
      ],
    });
  });

  async function serviceAt(url: string): Promise<FeedbackLinkService> {
    const service = TestBed.inject(FeedbackLinkService);
    await TestBed.inject(Router).navigateByUrl(url);
    return service;
  }

  it('builds the Cloud destination from the current product area', async () => {
    const service = await serviceAt('/library');
    expect(service.url()).toBe('https://nostos.page/feedback?from=library');
  });

  it('derives the reader origin from a reading route', async () => {
    const service = await serviceAt('/read/2f4b');
    expect(service.url()).toBe('https://nostos.page/feedback?from=reader');
  });

  it('keeps the capability default origin outside the product shell', async () => {
    const service = await serviceAt('/ui-catalogue');
    expect(service.url()).toBe('https://nostos.page/feedback?from=settings');
  });

  it('exposes no destination for SelfHosted', async () => {
    capabilities = selfHostedCapabilities;
    const service = await serviceAt('/library');
    expect(service.url()).toBeNull();
  });

  it('exposes no destination when capabilities fail to load', async () => {
    capabilitiesError = new Error('offline');
    const service = await serviceAt('/library');
    expect(service.url()).toBeNull();
  });
});
