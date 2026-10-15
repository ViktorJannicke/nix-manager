namespace NixManager.Nix;

/// <summary>
/// JavaScript's text semantics where the Bun version's results depend on them: the characters its regular
/// expressions call white space (<c>\s</c>) and "any character" (<c>.</c>), and <c>String.prototype.localeCompare</c>.
/// .NET's own <c>\s</c> differs (it has U+0085 and lacks U+FEFF), its <c>.</c> matches <c>\r</c>, and its <c>$</c>
/// matches before a final newline, so the ported patterns use these classes and <c>\z</c>.
/// </summary>
public static class JsText
{
    /// <summary>JavaScript's <c>\s</c>.</summary>
    public const string Space = @"[\t\n\v\f\r    -     　﻿]";

    /// <summary>JavaScript's <c>\S</c>.</summary>
    public const string NonSpace = @"[^\t\n\v\f\r    -     　﻿]";

    /// <summary>JavaScript's <c>.</c> without the <c>s</c> flag: anything but a line terminator.</summary>
    public const string Dot = @"[^\n\r  ]";

    // The CLDR root collation order of printable ASCII (ICU's, which Bun's localeCompare uses): white space,
    // punctuation, symbols, currency, digits, then letters with lower case before upper case at the third level.
    private const string Order = "\t\n\v\f\r _-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$0123456789abcdefghijklmnopqrstuvwxyz";

    private static (int Primary, int Secondary, int Tertiary)? Weight(char c)
    {
        if (c < 0x80)
        {
            var lower = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            var index = Order.IndexOf(lower);
            // Other control characters are ignorable.
            return index < 0 ? null : (index, 0, lower == c ? 0 : 1);
        }
        // A Latin letter with a diacritic sorts as its letter, after it at the second level (a < ä < b).
        var decomposed = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        if (decomposed.Length > 1 && decomposed[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' &&
            Weight(decomposed[0]) is { } letter)
            return (letter.Primary, 1, letter.Tertiary);
        return (Order.Length + c, 0, 0);
    }

    /// <summary>
    /// <c>a.localeCompare(b)</c> as Bun computes it for the default locale: exact for ASCII text such as file
    /// paths, and for Latin letters with diacritics; other characters sort after ASCII by code unit (ICU orders
    /// them by the full collation table).
    /// </summary>
    public static int LocaleCompare(string a, string b)
    {
        var x = a.Select(Weight).OfType<(int Primary, int Secondary, int Tertiary)>().ToList();
        var y = b.Select(Weight).OfType<(int Primary, int Secondary, int Tertiary)>().ToList();
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            if (x[i].Primary != y[i].Primary) return x[i].Primary < y[i].Primary ? -1 : 1;
        if (x.Count != y.Count) return x.Count < y.Count ? -1 : 1;
        for (var i = 0; i < x.Count; i++)
            if (x[i].Secondary != y[i].Secondary) return x[i].Secondary < y[i].Secondary ? -1 : 1;
        for (var i = 0; i < x.Count; i++)
            if (x[i].Tertiary != y[i].Tertiary) return x[i].Tertiary < y[i].Tertiary ? -1 : 1;
        return 0;
    }
}
