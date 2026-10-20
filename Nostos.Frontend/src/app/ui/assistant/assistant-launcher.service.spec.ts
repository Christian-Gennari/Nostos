import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { AssistantLauncherService } from './assistant-launcher.service';
import { AssistantService } from './assistant.service';
import { AssistantStatusService } from './assistant-status.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';

describe('AssistantLauncherService', () => {
  let launcher: AssistantLauncherService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    launcher = TestBed.inject(AssistantLauncherService);
  });

  function makeAvailable(): void {
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    TestBed.inject(AssistantStatusService).available.set(true);
  }

  it('is available only when the preference is on and the server can run it', () => {
    expect(launcher.available()).toBe(false);

    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    expect(launcher.available()).toBe(false);

    TestBed.inject(AssistantStatusService).available.set(true);
    expect(launcher.available()).toBe(true);
  });

  it('counts dock hosts, so a dock that leaves hands the launcher back', () => {
    expect(launcher.dockHosted()).toBe(false);

    const detachFirst = launcher.attachDock();
    const detachSecond = launcher.attachDock();
    expect(launcher.dockHosted()).toBe(true);

    detachFirst();
    // Detaching twice must not release the other dock's claim.
    detachFirst();
    expect(launcher.dockHosted()).toBe(true);

    detachSecond();
    expect(launcher.dockHosted()).toBe(false);
  });

  it('forwards a toggle only while Ask Nostos is available', () => {
    const toggles: number[] = [];
    launcher.toggleRequested.subscribe(() => toggles.push(1));

    launcher.toggle();
    expect(toggles.length).toBe(0);

    makeAvailable();
    launcher.toggle();
    expect(toggles.length).toBe(1);
  });

  it('labels the shortcut for the platform', () => {
    expect(launcher.shortcutLabel).toMatch(/^(⌘ J|Ctrl J)$/);
  });

  describe('before the deferred shell has loaded', () => {
    it('keeps a launcher press and asks for the shell', () => {
      const assistant = TestBed.inject(AssistantService);
      makeAvailable();

      launcher.toggle();

      expect(assistant.surfaceWanted()).toBe(true);
      expect(launcher.takePendingToggle()).toBe(true);
      // Taken once.
      expect(launcher.takePendingToggle()).toBe(false);
    });

    it('answers Cmd/Ctrl+J itself, and stands aside once the shell listens', () => {
      makeAvailable();
      launcher.start();

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'j', ctrlKey: true }));
      expect(launcher.takePendingToggle()).toBe(true);

      const toggles: number[] = [];
      launcher.toggleRequested.subscribe(() => toggles.push(1));
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'j', ctrlKey: true }));
      expect(toggles.length).toBe(0);
      expect(launcher.takePendingToggle()).toBe(false);
    });

    it('keeps a surface open request until the shell attaches', () => {
      const assistant = TestBed.inject(AssistantService);
      makeAvailable();

      assistant.requestSurfaceOpen();

      expect(assistant.surfaceWanted()).toBe(true);
      expect(assistant.takePendingSurfaceOpen()).toBe(true);
      expect(assistant.takePendingSurfaceOpen()).toBe(false);
    });
  });
});
