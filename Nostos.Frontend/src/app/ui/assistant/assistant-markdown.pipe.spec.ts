import { renderAssistantMarkdown } from './assistant-markdown.pipe';

/**
 * The marker typography is the reason this pipe has its own tests: the
 * component spec proves the rendered reply, and these cases pin the exact
 * character that stops a `<sup>` wrapping away from its word (#1562).
 */
function visibleText(html: string): string {
  return html.replace(/<[^>]*>/g, '');
}

describe('renderAssistantMarkdown citation markers', () => {
  it('glues a marker to the word before it with a narrow no-break space', () => {
    const html = renderAssistantMarkdown(
      'Attention, not time, is the thing you keep returning to.[^1]',
    );

    expect(html).toContain('returning to.\u202F<sup class="assistant-cite">');
    expect(html).not.toMatch(/[ \t]<sup class="assistant-cite">/);
    expect(visibleText(html)).toContain('returning to.\u202F1');
  });

  it('replaces the model’s own space before the marker instead of doubling it', () => {
    const html = renderAssistantMarkdown('A second claim [^2].');

    expect(html).toContain('claim\u202F<sup class="assistant-cite">');
    expect(html).not.toMatch(/[ \t]\u202F<sup class="assistant-cite">/);
    expect(visibleText(html)).toContain('A second claim\u202F2.');
  });

  it('keeps adjacent markers together and both numbered', () => {
    const html = renderAssistantMarkdown('Two grounds.[^1][^3]');

    expect(html).toContain('grounds.\u202F<sup class="assistant-cite">');
    expect(html.match(/<sup class="assistant-cite">/g)?.length).toBe(2);
    expect(visibleText(html)).toContain('grounds.\u202F1\u202F3');
  });

  it('leaves citation syntax inside code as code', () => {
    const html = renderAssistantMarkdown('Write `[^1]` for a footnote.');

    expect(html).not.toContain('<sup class="assistant-cite">');
    expect(html).toContain('Write <code>[^1]</code> for a footnote.');
  });
});
