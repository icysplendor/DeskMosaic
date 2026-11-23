using System.Text;

namespace WinAssistant.Translation;

internal static class ChineseEnglishSource
{
    internal static (string Source, string Target)? Detect(string text, bool provisional)
    {
        int chinese = 0, english = 0;
        string? first = null;
        bool inEnglishWord = false;
        // Match the macOS detector. One Han character or English word is
        // one vote, and punctuation, digits and apostrophes are neutral.
        foreach (var scalar in text.EnumerateRunes())
        {
            int code = scalar.Value;
            if (code is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF)
            {
                chinese++;
                first ??= "Chinese";
                inEnglishWord = false;
            }
            else if (code is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                if (!inEnglishWord) english++;
                first ??= "English";
                inEnglishWord = true;
            }
            else if (code is not (0x27 or 0x2019))
            {
                inEnglishWord = false;
            }
        }
        int total = chinese + english;
        if (provisional && (total < 4 || Math.Abs(chinese - english) < 2 || Math.Max(chinese, english) * 5 < total * 3))
            return null;
        string? language = chinese == english ? first : chinese > english ? "Chinese" : "English";
        return language is null ? null : (language, language == "Chinese" ? "English" : "Chinese");
    }
}
