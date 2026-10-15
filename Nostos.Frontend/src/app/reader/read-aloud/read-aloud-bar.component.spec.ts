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
});
