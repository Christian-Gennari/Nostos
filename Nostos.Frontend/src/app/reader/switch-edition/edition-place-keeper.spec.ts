import { Subject, throwError } from 'rxjs';

import { AlignmentService } from '../../core/services/alignment.service';
import { LocateAudioResult } from '../../core/dtos/alignment.dtos';
import { EditionPlaceKeeper, KeeperPlace, hasSwitched, rememberSwitched } from './edition-place-keeper';

describe('EditionPlaceKeeper', () => {
  const locateAudio = vi.fn();
  const alignment = { locateAudio } as unknown as AlignmentService;

  let place: KeeperPlace | null;
  let keeper: EditionPlaceKeeper;
  let response: Subject<LocateAudioResult>;

  const page = (excerpt: string): KeeperPlace => ({ bookId: 'epub-0', audioEditionId: 'audio-1', excerpt });

  async function settle(ms = EditionPlaceKeeper.SettleMs) {
    await vi.advanceTimersByTimeAsync(ms);
  }

  function answer() {
    response.next({ located: true, positionSeconds: 10, probes: 1, score: 1, anchorStored: true });
    response.complete();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    locateAudio.mockReset();
    locateAudio.mockImplementation(() => (response = new Subject<LocateAudioResult>()));
    place = page('It was the best of times.');
    keeper = new EditionPlaceKeeper(alignment, () => place);
    rememberSwitched('epub-0', 'audio-1');
  });

  afterEach(() => {
    keeper.stop();
    vi.useRealTimers();
  });

  it('locates the page the reader has settled on', async () => {
    keeper.moved();
    await settle(EditionPlaceKeeper.SettleMs - 1);
    expect(locateAudio).not.toHaveBeenCalled();

    await settle(1);
    expect(locateAudio).toHaveBeenCalledExactlyOnceWith('epub-0', {
      targetBookId: 'audio-1',
      excerpt: 'It was the best of times.',
    });
  });

  it('does nothing for a book the reader has never switched editions on', async () => {
    localStorage.clear();

    keeper.moved();
    await settle();

    expect(locateAudio).not.toHaveBeenCalled();
    expect(hasSwitched('epub-0')).toBe(false);
  });

  it('waits for the reader to stop turning pages', async () => {
    keeper.moved();
    await settle(15_000);
    place = page('It was the worst of times.');
    keeper.moved();
    await settle(15_000);
    expect(locateAudio).not.toHaveBeenCalled();

    await settle(5_000);
    expect(locateAudio).toHaveBeenCalledExactlyOnceWith('epub-0', {
      targetBookId: 'audio-1',
      excerpt: 'It was the worst of times.',
    });
  });

  it('locates at most once in the minimum gap', async () => {
    keeper.moved();
    await settle();
    answer();

    place = page('It was the age of wisdom.');
    keeper.moved();
    await settle();
    expect(locateAudio).toHaveBeenCalledTimes(1);

    await settle(EditionPlaceKeeper.MinimumGapMs);
    keeper.moved();
    await settle();
    expect(locateAudio).toHaveBeenCalledTimes(2);
  });

  it('does not locate the same page twice', async () => {
    keeper.moved();
    await settle();
    answer();

    await settle(EditionPlaceKeeper.MinimumGapMs);
    keeper.moved();
    await settle();

    expect(locateAudio).toHaveBeenCalledTimes(1);
  });

  it('does not start a second request while one is running', async () => {
    keeper.moved();
    await settle();

    await settle(EditionPlaceKeeper.MinimumGapMs);
    place = page('It was the age of foolishness.');
    keeper.moved();
    await settle();

    expect(locateAudio).toHaveBeenCalledTimes(1);
  });

  it('gives up for the session after a failure', async () => {
    locateAudio.mockImplementationOnce(() => throwError(() => new Error('429')));
    keeper.moved();
    await settle();
    expect(locateAudio).toHaveBeenCalledTimes(1);

    await settle(EditionPlaceKeeper.MinimumGapMs);
    place = page('It was the epoch of belief.');
    keeper.moved();
    await settle();

    expect(locateAudio).toHaveBeenCalledTimes(1);
  });

  it('stops at the session limit', async () => {
    for (let i = 0; i < EditionPlaceKeeper.MaximumPerSession + 3; i++) {
      place = page(`Page ${i}.`);
      keeper.moved();
      await settle();
      if (i < EditionPlaceKeeper.MaximumPerSession) answer();
      await settle(EditionPlaceKeeper.MinimumGapMs);
    }

    expect(locateAudio).toHaveBeenCalledTimes(EditionPlaceKeeper.MaximumPerSession);
  });

  it('does nothing on a page with no text, or with no audiobook edition', async () => {
    place = page('   ');
    keeper.moved();
    await settle();

    place = null;
    keeper.moved();
    await settle();

    expect(locateAudio).not.toHaveBeenCalled();
  });

  it('does nothing while the page is hidden', async () => {
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);

    keeper.moved();
    await settle();

    expect(locateAudio).not.toHaveBeenCalled();
    hidden.mockRestore();
  });

  it('cancels its request and its wait when stopped', async () => {
    keeper.moved();
    await settle();
    expect(response.observed).toBe(true);

    keeper.stop();
    expect(response.observed).toBe(false);

    place = page('It was the season of Light.');
    keeper.moved();
    keeper.stop();
    await settle(EditionPlaceKeeper.MinimumGapMs + EditionPlaceKeeper.SettleMs);
    expect(locateAudio).toHaveBeenCalledTimes(1);
  });

  it('reads a place that arrives asynchronously, as a PDF page does', async () => {
    keeper.stop();
    keeper = new EditionPlaceKeeper(alignment, () => Promise.resolve(page('A page of a PDF.')));

    keeper.moved();
    await settle();

    expect(locateAudio).toHaveBeenCalledExactlyOnceWith('epub-0', {
      targetBookId: 'audio-1',
      excerpt: 'A page of a PDF.',
    });
  });
});
