import { passageBodyHtml, passageSourceParams } from './passage-viewer.helpers';

describe('passage viewer helpers (issue #1014)', () => {
  it('uses the saved markup, with the reader note opening the first page', () => {
    expect(passageBodyHtml({ html: '<p>Imagine, <em>then</em></p>', text: 'Imagine, then', content: '' })).toBe(
      '<p>Imagine, <em>then</em></p>',
    );
    expect(passageBodyHtml({ html: '<p>x</p>', text: 'x', content: 'Why <this>?' })).toBe(
      '<aside class="pv-note">Why &lt;this&gt;?</aside><p>x</p>',
    );
  });

  it('shows a topic link in the note as its words, not its markup', () => {
    expect(passageBodyHtml({ html: '<p>x</p>', text: 'x', content: 'About [[Freedom]] and [[Grace|grace]].' })).toBe(
      '<aside class="pv-note">About Freedom and grace.</aside><p>x</p>',
    );
  });

  it('sets plain text as escaped paragraphs when there is no markup', () => {
    expect(passageBodyHtml({ html: null, text: 'One  line\nwraps.\n\nTwo & <b>', content: '' })).toBe(
      '<p>One line wraps.</p><p>Two &amp; &lt;b&gt;</p>',
    );
  });

  it('has nothing to show for a passage without text, note or not', () => {
    expect(passageBodyHtml({ html: null, text: null, content: 'A note' })).toBe('');
  });

  it('opens the book at the start of the passage, in its own terms', () => {
    expect(passageSourceParams({ anchorKind: 'epub_cfi', start: 'epubcfi(/6/4!/4/2/1:0)' })).toEqual({
      sourceCfi: 'epubcfi(/6/4!/4/2/1:0)',
    });
    expect(passageSourceParams({ anchorKind: 'pdf_page', start: '241' })).toEqual({ sourcePage: 241 });
    expect(passageSourceParams({ anchorKind: 'audio_timestamp', start: '3600.5' })).toEqual({ sourceTime: 3600.5 });
    expect(passageSourceParams({ anchorKind: 'pdf_page', start: '0' })).toBeNull();
    expect(passageSourceParams({ anchorKind: 'unknown', start: 'x' })).toBeNull();
  });
});
