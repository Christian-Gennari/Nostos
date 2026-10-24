import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { READ_ALOUD_RING_DELAY_MS, ReadAloudBarComponent, readAloudVoiceLabel } from './read-aloud-bar.component';
import { ReadAloudController } from './read-aloud-controller';

describe('readAloudVoiceLabel', () => {
  const heart = { id: 'af_heart', name: 'Heart', language: 'en-US' };
  const emma = { id: 'bf_emma', name: 'Emma', language: 'en-GB' };
  const sara = { id: 'if_sara', name: 'Sara', language: 'it' };

  it('names the voice alone when every voice has the same accent', () => {
    expect(readAloudVoiceLabel(heart, [heart])).toBe('Heart');
  });

  it('adds the accent when one language has several', () => {
    expect(readAloudVoiceLabel(emma, [heart, emma])).toBe('Emma · British');
  });

  it('names the language when the voices span several', () => {
    expect(readAloudVoiceLabel(sara, [heart, emma, sara])).toBe('Sara · Italian');
    expect(readAloudVoiceLabel(emma, [heart, emma, sara])).toBe('Emma · British English');
  });
});

describe('ReadAloudBarComponent', () => {
  const buffering = signal(false);

  function render() {
    TestBed.configureTestingModule({
      imports: [ReadAloudBarComponent],
      providers: [
        {
          provide: ReadAloudController,
          useValue: {
            active: signal(true),
            playing: signal(true),
            buffering,
            rate: signal(1),
            notice: signal(null),
            activeVoiceId: signal('browser'),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ReadAloudBarComponent);
    fixture.detectChanges();
    const toggle = () =>
      fixture.nativeElement.querySelector('[data-testid="read-aloud-toggle"]') as HTMLElement;
    return { fixture, toggle };
  }

  beforeEach(() => {
    vi.useFakeTimers();
    buffering.set(false);
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
  });

  it('shows the ring only once a sentence has been waited on for a second', async () => {
    const { fixture, toggle } = render();

    buffering.set(true);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(READ_ALOUD_RING_DELAY_MS - 1);
    fixture.detectChanges();
    expect(toggle().classList).not.toContain('is-buffering');
    expect(toggle().getAttribute('aria-busy')).toBe('false');

    await vi.advanceTimersByTimeAsync(1);
    fixture.detectChanges();
    expect(toggle().classList).toContain('is-buffering');
    expect(toggle().getAttribute('aria-busy')).toBe('true');

    buffering.set(false);
    fixture.detectChanges();
    expect(toggle().classList).not.toContain('is-buffering');
  });

  it('never draws the ring between sentences that arrive quickly', async () => {
    const { fixture, toggle } = render();

    for (let sentence = 0; sentence < 5; sentence++) {
      buffering.set(true);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(300);
      fixture.detectChanges();
      expect(toggle().classList).not.toContain('is-buffering');
      buffering.set(false);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(800);
    }
    await vi.advanceTimersByTimeAsync(READ_ALOUD_RING_DELAY_MS);
    fixture.detectChanges();
    expect(toggle().classList).not.toContain('is-buffering');
  });

  it('puts the transport away with the chrome', () => {
    const { fixture } = render();
    const bar = () => fixture.nativeElement.querySelector('[data-testid="read-aloud-bar"]') as HTMLElement;
    expect(bar().classList).not.toContain('is-resting');
    expect(bar().hasAttribute('inert')).toBe(false);

    fixture.componentRef.setInput('resting', true);
    fixture.detectChanges();
    expect(bar().classList).toContain('is-resting');
    expect(bar().hasAttribute('inert')).toBe(true);
    expect(bar().getAttribute('aria-hidden')).toBe('true');
  });

  it('chooses the speed in the shared playback-speed menu, its range the voices support (issue #1200)', () => {
    const { fixture } = render();
    const controller = TestBed.inject(ReadAloudController) as unknown as { setRate: (r: number) => void };
    controller.setRate = vi.fn();
    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="read-aloud-rate"] [data-testid="playback-speed-trigger"]',
    ) as HTMLButtonElement;
    expect(trigger.getAttribute('aria-label')).toBe('Reading speed: 1×. Change speed');

    trigger.click();
    fixture.detectChanges();
    const choices = Array.from(document.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]'));
    expect(choices.map((c) => c.textContent?.trim())).toEqual(['0.75×', '1×', '1.25×', '1.5×']);
    // Read aloud has no fine step: its voices take these rates only.
    expect(document.querySelector('[data-testid="playback-speed-faster"]')).toBeNull();

    choices[2].click();
    expect(controller.setRate).toHaveBeenCalledWith(1.25);
    document.querySelectorAll('.cdk-overlay-container').forEach((el) => el.remove());
  });
});
