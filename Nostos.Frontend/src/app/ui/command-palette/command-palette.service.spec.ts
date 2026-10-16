import { TestBed } from '@angular/core/testing';

import { CommandPaletteService } from './command-palette.service';

describe('CommandPaletteService', () => {
  let palette: CommandPaletteService;

  beforeEach(() => {
    palette = TestBed.inject(CommandPaletteService);
  });

  const press = (init: KeyboardEventInit) => {
    const event = new KeyboardEvent('keydown', { cancelable: true, ...init });
    document.dispatchEvent(event);
    return event;
  };

  it('owns Cmd/Ctrl+K once started, before any palette component exists', () => {
    expect(press({ key: 'k', ctrlKey: true }).defaultPrevented).toBe(false);

    palette.start();
    palette.start();
    const event = press({ key: 'k', ctrlKey: true });
    expect(event.defaultPrevented).toBe(true);
    // Started twice, toggled once.
    expect(palette.isOpen()).toBe(true);

    press({ key: 'K', metaKey: true, shiftKey: true });
    expect(palette.isOpen()).toBe(false);
  });

  it('leaves Alt chords and other keys alone', () => {
    palette.start();
    expect(press({ key: 'k', ctrlKey: true, altKey: true }).defaultPrevented).toBe(false);
    expect(press({ key: 'j', ctrlKey: true }).defaultPrevented).toBe(false);
    expect(palette.isOpen()).toBe(false);
  });

  it('closes through the palette when it has registered a dismissal', () => {
    const dismiss = vi.fn(() => palette.close());
    palette.setDismissHandler(dismiss);
    palette.open();

    palette.toggle();
    expect(dismiss).toHaveBeenCalledTimes(1);
    expect(palette.isOpen()).toBe(false);
  });

  it('stops listening when the app is torn down', () => {
    palette.start();
    TestBed.resetTestingModule();
    expect(press({ key: 'k', ctrlKey: true }).defaultPrevented).toBe(false);
  });
});
