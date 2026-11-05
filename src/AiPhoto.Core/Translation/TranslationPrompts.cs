namespace AiPhoto.Core.Translation;

public static class TranslationPrompts
{
    public static string BuildSystemPrompt(string targetLanguage, bool skipTextInTargetLanguage, string? customInstructions)
    {
        var skipRule = skipTextInTargetLanguage
            ? $"- Omit blocks whose text is already entirely written in {targetLanguage}."
            : $"- Include blocks already written in {targetLanguage}; for them translation = source.";

        var prompt = $$"""
            You are the translation engine of a screenshot translation tool. The user sends a screenshot.
            Read ALL legible text in the image directly (do not describe the image), group it into logical
            blocks and translate every block into {{targetLanguage}}.

            Output: a JSON array; each element is {"box_2d": [ymin, xmin, ymax, xmax], "source": "...", "translation": "..."}.

            Rules:
            - box_2d is normalised to 0-1000 relative to the image height (y) and width (x) and must tightly
              enclose the original glyphs of the block (not the surrounding button, bubble or panel).
            - A block is one visually coherent unit: a paragraph, a single UI label or button, a menu item,
              a table cell, a speech bubble, a caption. Lines of the same paragraph form ONE block.
              Never merge text from visually separate areas into one block.
            - source: the original text exactly as shown, using \n for its line breaks.
            - translation: fluent, natural {{targetLanguage}} that preserves meaning and tone. Use the context of the
              whole image (application UI, game, manga, document, code, chat...) to choose correct terminology.
              Keep numbers, product names, code, file paths, URLs and keyboard shortcuts unchanged.
              Keep the translation roughly as long as the source so it fits in the same area.
            {{skipRule}}
            - Do not skip small, low-contrast, rotated, vertical or stylised text.
            - Order blocks in natural reading order.
            - If the image contains no text, return [].
            """;

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            prompt += "\n\nAdditional instructions from the user:\n" + customInstructions.Trim();
        }
        return prompt;
    }

    public static string BuildUserPrompt(string targetLanguage)
        => $"Translate all text in this screenshot into {targetLanguage}.";
}
