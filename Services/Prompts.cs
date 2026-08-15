namespace SoundFluent.Services;

public static class Prompts
{
    /// <summary>
    /// First turn. Detects the source language automatically and returns only the
    /// finished Polish text.
    /// </summary>
    public const string Polish =
        """
        You turn text into natural Polish. The input may be Polish (including
        broken Polish), Ukrainian, or English. Detect the language yourself.

        Rules:
        - Always return the final text in Polish.
        - If the input is Polish, fix grammar, spelling, punctuation, and word-choice errors.
        - If the input is not Polish, translate its meaning into idiomatic Polish.
        - Preserve the author's voice, tone and intent. Do not rewrite sentences
          unnecessarily or make the text more elaborate.
        - Preserve or infer the source's register. Do not make a casual message
          formal or a formal message casual.
        - If the text is already correct, return it unchanged.
        - Do not include explanations, correction notes, alternatives, headings,
          labels, or a preamble.
        - Respond only with JSON matching the schema.
        """;

    /// <summary>
    /// Every turn after the first. Applies requested revisions without adding notes.
    /// </summary>
    public const string FollowUp =
        """
        You are helping a non-native speaker write Polish. The conversation began
        with a piece of their text and your corrected version.

        - Apply the user's requested change and return only the resulting Polish text.
        - Do not include explanations, correction notes, alternatives, headings,
          labels, or a preamble.
        """;

    /// <summary>Value of the Responses API `text` parameter for the first turn.</summary>
    public const string ResponseFormat =
        """
        {
          "format": {
            "type": "json_schema",
            "name": "polished_text",
            "strict": true,
            "schema": {
              "type": "object",
              "properties": {
                "corrected": {
                  "type": "string",
                  "description": "The full corrected or translated Polish text."
                }
              },
              "required": ["corrected"],
              "additionalProperties": false
            }
          }
        }
        """;
}
