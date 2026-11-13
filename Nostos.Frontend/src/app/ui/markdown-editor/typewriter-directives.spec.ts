import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Editor } from '@tiptap/core';

import { SoundPack } from './typewriter-sounds';
import { TypewriterScrollDirective } from './typewriter-scroll.directive';
import { TypewriterSoundDirective } from './typewriter-sound.directive';

@Component({
  standalone: true,
  imports: [TypewriterScrollDirective, TypewriterSoundDirective],
  template: `
    <div
      class="scroller"
      [appTypewriterScroll]="typewriter()"
      [typewriterEditor]="editor()"
      [appTypewriterSound]="sounds()"
      [typewriterSoundVolume]="volume()"
      [typewriterSoundPack]="pack()"
      [typewriterSoundTarget]="target()"
      [typewriterSuppressReturn]="suppressReturn()"
    ></div>
  `,
})
class TypewriterHostComponent {
  readonly typewriter = signal(false);
  readonly editor = signal<Editor | null>(null);
  readonly sounds = signal(false);
  readonly volume = signal(0.6);
  readonly pack = signal<SoundPack>('typewriter');
  readonly target = signal<HTMLElement | null>(null);
  readonly suppressReturn = signal(false);
}

describe('Typewriter directives', () => {
  let fixture: ComponentFixture<TypewriterHostComponent>;
  let host: TypewriterHostComponent;
  let scroller: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TypewriterHostComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(TypewriterHostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
    scroller = fixture.nativeElement.querySelector('.scroller') as HTMLElement;
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    fixture?.destroy();
  });

  describe('TypewriterScrollDirective', () => {
    it('toggles the typewriter class and scrolls caret toward 45% on update/selectionUpdate', () => {
      const listeners = new Map<string, Set<() => void>>();
      const scrollBy = vi.fn();
      scroller.scrollBy = scrollBy;
      vi.spyOn(scroller, 'getBoundingClientRect').mockReturnValue({ top: 0 } as DOMRect);
      Object.defineProperty(scroller, 'clientHeight', { value: 1000, configurable: true });

      let caretTop = 900;
      const fakeEditor = {
        isDestroyed: false,
        state: { selection: { head: 1 } },
        view: { coordsAtPos: () => ({ top: caretTop }) },
        on: vi.fn((event: string, fn: () => void) => {
          if (!listeners.has(event)) listeners.set(event, new Set());
          listeners.get(event)!.add(fn);
        }),
        off: vi.fn((event: string, fn: () => void) => {
          listeners.get(event)?.delete(fn);
        }),
      } as unknown as Editor;

      host.editor.set(fakeEditor);
      host.typewriter.set(true);
      fixture.detectChanges();

      expect(scroller.classList.contains('typewriter')).toBe(true);
      expect(listeners.get('update')?.size).toBe(1);
      expect(listeners.get('selectionUpdate')?.size).toBe(1);

      caretTop = 900;
      for (const fn of listeners.get('selectionUpdate') ?? []) fn();
      expect(scrollBy).toHaveBeenCalledWith({ top: 450 });

      fixture.destroy();
      expect(listeners.get('update')?.size).toBe(0);
      expect(listeners.get('selectionUpdate')?.size).toBe(0);
    });
  });

  describe('TypewriterSoundDirective', () => {
    let started: number;
    let closed: number;

    beforeEach(() => {
      started = 0;
      closed = 0;
      class FakeContext {
        state = 'running';
        destination = {};
        createBufferSource() {
          return { buffer: null, playbackRate: { value: 1 }, connect: () => undefined, start: () => started++ };
        }
        createGain() {
          return { gain: { value: 1 }, connect: () => undefined };
        }
        decodeAudioData() {
          return Promise.resolve({} as AudioBuffer);
        }
        resume() {
          return Promise.resolve();
        }
        suspend() {
          return Promise.resolve();
        }
        close() {
          closed++;
          return Promise.resolve();
        }
      }
      vi.stubGlobal('AudioContext', FakeContext);
      vi.stubGlobal(
        'fetch',
        vi.fn(() => Promise.resolve({ ok: true, arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)) })),
      );
    });

    it('wires key listeners onto target, suppresses return when requested, and disposes on destroy', async () => {
      const target = document.createElement('div');
      host.target.set(target);
      host.sounds.set(true);
      fixture.detectChanges();

      // Wait for prefetch/decode on first key.
      target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      await new Promise((r) => setTimeout(r));
      started = 0;

      // Return plays when not suppressed.
      target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      expect(started).toBe(1);

      // Return is suppressed while an overlay is open.
      host.suppressReturn.set(true);
      fixture.detectChanges();
      target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      expect(started).toBe(1);

      // Disposes audio context and detaches listeners on destroy.
      fixture.destroy();
      expect(closed).toBe(1);
      target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Backspace' }));
      expect(started).toBe(1);
    });
  });
});
