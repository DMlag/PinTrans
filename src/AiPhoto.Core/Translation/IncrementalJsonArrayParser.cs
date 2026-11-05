using System.Text;

namespace AiPhoto.Core.Translation;

/// <summary>
/// Extracts complete elements of a top-level JSON array from a text stream that arrives in arbitrary chunks,
/// so translated blocks can be shown while the model is still generating the rest of the answer.
/// Tolerates leading noise such as markdown code fences.
/// </summary>
public sealed class IncrementalJsonArrayParser
{
    private readonly StringBuilder _element = new();
    private bool _arrayStarted;
    private bool _arrayEnded;
    private int _depth;          // nesting depth inside the current element
    private bool _inString;
    private bool _escape;
    private bool _inScalar;      // inside a top-level number / true / false / null element

    public bool ArrayEnded => _arrayEnded;

    /// <summary>Feeds more text and returns the raw JSON of each element completed by it.</summary>
    public IReadOnlyList<string> Feed(string chunk)
    {
        List<string>? completed = null;

        foreach (var ch in chunk)
        {
            if (_arrayEnded) break;

            if (!_arrayStarted)
            {
                if (ch == '[') _arrayStarted = true;
                continue;
            }

            if (_inScalar)
            {
                // A top-level scalar ends at the next separator; the separator itself is handled below.
                if (char.IsWhiteSpace(ch) || ch == ',' || ch == ']')
                {
                    _inScalar = false;
                    Complete(ref completed);
                }
                else
                {
                    _element.Append(ch);
                    continue;
                }
            }

            // Note: must not run inside a top-level string element, otherwise ',' / ']' / spaces in it would be
            // treated as separators (previously "[\"a]b\"]" ended the array at the ']' inside the string).
            if (_depth == 0 && !_inString)
            {
                // Between elements: skip whitespace and commas, detect end of array or start of a value.
                if (char.IsWhiteSpace(ch) || ch == ',') continue;
                if (ch == ']') { _arrayEnded = true; continue; }
                if (ch is not ('{' or '[' or '"'))
                {
                    // Start of a scalar element. Previously these were never completed and got glued to the
                    // following element, corrupting it.
                    _inScalar = true;
                    _element.Append(ch);
                    continue;
                }
            }

            _element.Append(ch);

            if (_inString)
            {
                if (_escape) _escape = false;
                else if (ch == '\\') _escape = true;
                else if (ch == '"')
                {
                    _inString = false;
                    if (_depth == 0) Complete(ref completed); // top-level string element
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    _inString = true;
                    break;
                case '{':
                case '[':
                    _depth++;
                    break;
                case '}':
                case ']':
                    _depth--;
                    if (_depth == 0) Complete(ref completed);
                    break;
            }
        }

        return (IReadOnlyList<string>?)completed ?? [];
    }

    private void Complete(ref List<string>? completed)
    {
        (completed ??= []).Add(_element.ToString());
        _element.Clear();
    }
}
