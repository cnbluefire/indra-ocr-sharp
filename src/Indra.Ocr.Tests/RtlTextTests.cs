using Indra.Ocr.Text;

namespace Indra.Ocr.Tests;

public class RtlTextTests
{
    private const string Arabic = "مرحبا";
    private const string Hebrew = "שלום";

    [Fact]
    public void Flip_reverses_rtl_and_keeps_ltr_runs()
    {
        Assert.Equal(string.Concat(Arabic.Reverse()), RtlText.Flip(Arabic));
        string visual = RtlText.Flip($"{Arabic} 2026-10-13 {Hebrew} abc def");
        Assert.Contains("abc def", visual);
        Assert.Contains("2026-10-13", visual);
        Assert.True(visual.IndexOf("abc def", StringComparison.Ordinal) <
                    visual.IndexOf(string.Concat(Hebrew.Reverse()), StringComparison.Ordinal));
        Assert.Equal($"the word {string.Concat(Hebrew.Reverse())} means peace",
                     RtlText.Flip($"the word {Hebrew} means peace"));
    }

    [Fact]
    public void Flip_is_its_own_inverse()
    {
        string[] cases =
        [
            Arabic + " 2026-10-13 " + Hebrew + " abc def",
            "abc (" + Hebrew + ") ١٩",
            "َ" + Arabic + " [] 019",
        ];
        foreach (string value in cases)
        {
            string normalized = RtlText.Normalize(value);
            Assert.Equal(normalized, RtlText.Flip(RtlText.Flip(normalized)));
        }
    }

    [Fact]
    public void Normalize_decomposes_presentation_forms_without_icu()
    {
        Assert.Equal("لا", RtlText.Normalize("ﻻ"));
        Assert.Equal("יִ", RtlText.Normalize("יִ"));
        Assert.Equal("اب", RtlText.Normalize("ـاب"));
    }
}
