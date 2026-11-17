using System;
using System.Globalization;
using System.Text;

namespace OpenSlideSharp;

/// <summary>
/// A minimal writer for compact JSON (no whitespace), so the core package needs no JSON library. It inserts the
/// commas between values but does not check that the calls form a well-nested document; callers write the
/// structure they mean. Strings are escaped as RFC 8259 requires; other characters are written as they are.
/// </summary>
internal sealed class JsonText
{
    private readonly StringBuilder _text = new();
    private bool _afterValue; // a value (or a closed container) was just written, so the next one needs a comma

    internal JsonText StartObject() => Open('{');

    internal JsonText EndObject() => Close('}');

    internal JsonText StartArray() => Open('[');

    internal JsonText EndArray() => Close(']');

    /// <summary>Writes a property name and the colon; the next call writes its value.</summary>
    internal JsonText Name(string name)
    {
        Separate();
        AppendString(name);
        _text.Append(':');
        _afterValue = false;
        return this;
    }

    internal JsonText Value(string value)
    {
        Separate();
        AppendString(value);
        _afterValue = true;
        return this;
    }

    internal JsonText Value(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Writes a finite number with the round-trip (<c>"R"</c>) invariant format.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite, which JSON cannot represent.</exception>
    internal JsonText Value(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "JSON numbers must be finite.");
        }

        return Raw(value.ToString("R", CultureInfo.InvariantCulture));
    }

    internal JsonText Value(bool value) => Raw(value ? "true" : "false");

    internal JsonText Null() => Raw("null");

    internal JsonText Property(string name, string value) => Name(name).Value(value);

    internal JsonText Property(string name, long value) => Name(name).Value(value);

    internal JsonText Property(string name, bool value) => Name(name).Value(value);

    /// <summary>Writes an array of integers.</summary>
    internal JsonText Numbers(params long[] values)
    {
        StartArray();
        foreach (long value in values)
        {
            Value(value);
        }

        return EndArray();
    }

    /// <summary>Writes an array of finite numbers.</summary>
    internal JsonText Numbers(params double[] values)
    {
        StartArray();
        foreach (double value in values)
        {
            Value(value);
        }

        return EndArray();
    }

    /// <summary>Returns the text written so far.</summary>
    public override string ToString() => _text.ToString();

    /// <summary>Returns the text written so far, encoded as UTF-8 without a byte order mark.</summary>
    internal byte[] ToUtf8() => Encoding.UTF8.GetBytes(_text.ToString());

    private JsonText Open(char bracket)
    {
        Separate();
        _text.Append(bracket);
        _afterValue = false;
        return this;
    }

    private JsonText Close(char bracket)
    {
        _text.Append(bracket);
        _afterValue = true;
        return this;
    }

    private JsonText Raw(string token)
    {
        Separate();
        _text.Append(token);
        _afterValue = true;
        return this;
    }

    private void Separate()
    {
        if (_afterValue)
        {
            _text.Append(',');
        }
    }

    private void AppendString(string value)
    {
        _text.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': _text.Append("\\\""); break;
                case '\\': _text.Append("\\\\"); break;
                case '\b': _text.Append("\\b"); break;
                case '\f': _text.Append("\\f"); break;
                case '\n': _text.Append("\\n"); break;
                case '\r': _text.Append("\\r"); break;
                case '\t': _text.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _text.Append(c);
                    }

                    break;
            }
        }

        _text.Append('"');
    }
}
