/**
 * What the AI-suggestion surfaces say when a proposal turn ends (issue #1665).
 *
 * The sorter and the shared topic picker both read these, so a note that gets
 * no suggestions cannot say one thing on one surface and something else on
 * another. A turn that simply found nothing is not an outage: it says so, while
 * a real failure gets the failure sentence and a "Try again" beside it.
 */
export const NO_TOPIC_SUGGESTIONS = 'No topics suggest themselves for this note.';
export const TOPIC_SUGGESTIONS_FAILED = 'Could not get suggestions.';
