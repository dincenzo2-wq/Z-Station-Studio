using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ZStationStudio.Services
{
    public static class SearchNormalizer
    {
        public static string Normalize(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            string normalizedString = input.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(normalizedString.Length);

            foreach (char c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    if (c == 'đ' || c == 'Đ')
                    {
                        stringBuilder.Append('d');
                    }
                    else
                    {
                        stringBuilder.Append(c);
                    }
                }
            }

            string result = stringBuilder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

            // Replace punctuation and separators with space
            result = Regex.Replace(result, @"[_\-.()[\]{}<>,;:/\\+]+", " ");
            result = Regex.Replace(result, @"\s+", " ").Trim();

            return result;
        }

        public static bool MatchesAllTerms(string normalizedTarget, string[] normalizedQueryTerms)
        {
            if (normalizedQueryTerms.Length == 0) return true;
            for (int i = 0; i < normalizedQueryTerms.Length; i++)
            {
                if (!normalizedTarget.Contains(normalizedQueryTerms[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
