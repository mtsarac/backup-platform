using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Lists;

/// <summary>
/// The one search fold of every list: lower case, <c>İ I ı i</c> all equal <c>i</c> and <c>ş ğ ç ö ü</c> equal <c>s g c o u</c>, so
/// <c>INDIGO</c>, <c>İNDİGO</c>, <c>Şifreli</c> and <c>sifreli</c> match each other. The term is folded here, the column side in SQL by
/// <see cref="Translate"/> (Postgres <c>translate</c>, mapped in <see cref="RegisterWith"/>) and <c>lower</c>, so both sides fold alike.
/// The frontend's <c>foldSearch</c> applies the same table; <c>TextFoldTests</c> and its frontend twin share the sample strings.
/// </summary>
public static class TextFold
{
    // Replaced before lower-casing, so a locale-dependent lower() (tr_TR turns I into ı, ICU turns İ into i + U+0307) never sees them.
    public const string From = "İIıŞşĞğÇçÖöÜü";
    public const string To = "iiissggccoouu";

    public static string Fold(string text) => Translate(text, From, To)!.ToLowerInvariant();

    /// <summary>Postgres <c>translate(text, from, to)</c>: each character of <paramref name="from"/> becomes the one at the same index of <paramref name="to"/>.</summary>
    public static string? Translate(string? text, string from, string to) =>
        text is null ? null : string.Create(text.Length, (text, from, to), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var index = state.from.IndexOf(state.text[i]);
                span[i] = index < 0 ? state.text[i] : state.to[index];
            }
        });

    /// <summary>Postgres <c>regexp_replace(text, pattern, replacement, flags)</c>; only the <c>g</c> flag (replace every match) is used.</summary>
    public static string? RegexpReplace(string? text, string pattern, string replacement, string flags) =>
        text is null ? null : Regex.Replace(text, pattern, replacement);

    public static void RegisterWith(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(TextFold).GetMethod(nameof(Translate))!).HasName("translate").IsBuiltIn();
        modelBuilder.HasDbFunction(typeof(TextFold).GetMethod(nameof(RegexpReplace))!).HasName("regexp_replace").IsBuiltIn();
    }
}
