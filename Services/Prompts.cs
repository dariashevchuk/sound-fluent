namespace SoundFluent.Services;

public static class Prompts
{
    /// <summary>
    /// First turn. Returns structured JSON so the window can show a diff and an
    /// error list, not just replacement text.
    /// </summary>
    public static string Correct(Register register) =>
        """
        You correct Polish text written by a non-native speaker.

        Rules:
        - Fix grammar, spelling, punctuation, and word-choice errors.
        - Preserve the author's voice, tone and intent. Do not rewrite sentences
          that are already correct, and do not make the text more elaborate.
        - If the text is already correct, return it unchanged with an empty error list.
        - Explain each error in English, naming the Polish grammatical concept, e.g.
          "accusative after 'widzieć', not nominative" or "perfective aspect: the
          action is finished".
        - Keep each explanation under about twelve words.
        - Respond only with JSON matching the schema.
        """ + register switch
        {
            Register.Informal => "\n- Target register: informal (per 'ty'), the way you would write to a friend.",
            Register.Formal => "\n- Target register: formal (per 'pan'/'pani'), suitable for work or officials.",
            _ => "\n- Keep whatever register the author used. Do not shift it."
        };

    /// <summary>
    /// Every turn after the first. Free-form, because follow-ups are things like
    /// "shorter", "why that ending?", "make it sound less stiff".
    /// </summary>
    public const string FollowUp =
        """
        You are helping a non-native speaker write Polish. The conversation began
        with a piece of their text and your corrected version.

        - If asked to change the text, reply with the new Polish version first, on
          its own, with no preamble. Add a one-line note underneath only if a change
          is worth explaining.
        - If asked about a rule, explain briefly in English with a concrete example.
        - Be concise. This is a small utility window, not an essay.
        """;

    /// <summary>
    /// Translate mode. Returns plain text — a diff between English and Polish
    /// would be noise, and there are no "errors" to list.
    /// </summary>
    public static string Translate(Register register) =>
        """
        You translate text into Polish. The person will send your output as their
        own message, so it has to read as something a Pole would actually write.

        Rules:
        - Reply with the Polish only. No preamble, no quotation marks, no notes.
        - Translate the meaning, not the words. Idiomatic beats literal.
        - Detect the source language yourself. It may be English or Ukrainian.
        - If the input is already Polish, correct it and return the corrected version.
        - Match the length and tone of the original. Don't make a casual line formal.
        """ + register switch
        {
            Register.Informal => "\n- Target register: informal (per 'ty').",
            Register.Formal => "\n- Target register: formal (per 'pan'/'pani').",
            _ => "\n- Infer the register from the source text and keep it."
        };

    /// <summary>Value of the Responses API `text` parameter for the first turn.</summary>
    public const string ResponseFormat =
        """
        {
          "format": {
            "type": "json_schema",
            "name": "correction",
            "strict": true,
            "schema": {
              "type": "object",
              "properties": {
                "corrected": {
                  "type": "string",
                  "description": "The full corrected Polish text."
                },
                "errors": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "original": { "type": "string" },
                      "fixed":    { "type": "string" },
                      "rule":     { "type": "string" }
                    },
                    "required": ["original", "fixed", "rule"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["corrected", "errors"],
              "additionalProperties": false
            }
          }
        }
        """;
}

public enum Register
{
    Keep = 0,
    Informal = 1,
    Formal = 2
}

public enum Mode
{
    /// <summary>Input is Polish. Fix it and show what changed.</summary>
    Correct = 0,

    /// <summary>Input is another language. Produce Polish.</summary>
    Translate = 1
}
