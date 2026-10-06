using SwitchPilot.Core.Text;

namespace SwitchPilot.Tests;

/// <summary>Release notes Markdown (update window).</summary>
public class MarkdownTests
{
    private static IReadOnlyList<MdInline> Inline(string text) => Markdown.Inlines(text);

    [Fact]
    public void BoldItalicCodeAndStrike()
    {
        var inlines = Inline("Switch Pilot **1.1.0** est *multi* `show version` ~~ancien~~ ***fort***");
        Assert.Equal("Switch Pilot ", ((MdText)inlines[0]).Text);
        Assert.Equal("1.1.0", Markdown.PlainText(((MdStrong)inlines[1]).Content));
        Assert.Equal("multi", Markdown.PlainText(((MdEmphasis)inlines[3]).Content));
        Assert.Equal("show version", ((MdCode)inlines[5]).Code);
        Assert.Equal("ancien", Markdown.PlainText(((MdStrike)inlines[7]).Content));
        Assert.IsType<MdEmphasis>(((MdStrong)inlines[9]).Content.Single());
        Assert.Equal("Switch Pilot 1.1.0 est multi show version ancien fort", Markdown.PlainText(inlines));
    }

    [Fact]
    public void NestedAndUnderscoreEmphasis()
    {
        var strong = (MdStrong)Inline("**a *b* c**").Single();
        Assert.IsType<MdEmphasis>(strong.Content[1]);
        var emphasis = (MdEmphasis)Inline("*a **b** c*").Single();
        Assert.IsType<MdStrong>(emphasis.Content[1]);
        Assert.IsType<MdStrong>(Inline("__gras__").Single());
        // Identifiers keep their underscores; isolated stars stay literal.
        Assert.Equal("port_overrides et 2 * 3 * 4", ((MdText)Inline("port_overrides et 2 * 3 * 4").Single()).Text);
        Assert.Equal("**pas fermé", ((MdText)Inline("**pas fermé").Single()).Text);
    }

    [Fact]
    public void CodeSpansAreLiteral()
    {
        var code = (MdCode)Inline("`**pas gras** et _x_`").Single();
        Assert.Equal("**pas gras** et _x_", code.Code);
        Assert.Equal("a ` b", ((MdCode)Inline("`` a ` b ``").Single()).Code);
        Assert.Equal("*littéral*", ((MdText)Inline(@"\*littéral\*").Single()).Text);
    }

    [Fact]
    public void LinksOnlyOpenWebAddresses()
    {
        var link = (MdLink)Inline("[Installation](https://github.com/FIlox77250/SwitchPilot/blob/v1.1.0/docs/INSTALLATION.md \"titre\")").Single();
        Assert.Equal("https://github.com/FIlox77250/SwitchPilot/blob/v1.1.0/docs/INSTALLATION.md", link.Url);
        Assert.Equal("Installation", Markdown.PlainText(link.Content));
        Assert.Null(((MdLink)Inline("[piège](file:///C:/Windows/System32/calc.exe)").Single()).Url);
        Assert.Null(((MdLink)Inline("[script](javascript:alert(1))").Single()).Url);
        Assert.Null(((MdLink)Inline("[relatif](docs/x.md)").Single()).Url);
        Assert.IsType<MdStrong>(((MdLink)Inline("[**Gras**](https://a.example)").Single()).Content.Single());
    }

    [Fact]
    public void AutolinksAndBreaks()
    {
        var inlines = Inline("Voir https://help.mikrotik.com/docs/x. Et <https://a.example/b> (https://c.example/d)");
        var urls = inlines.OfType<MdLink>().Select(l => l.Url).ToList();
        Assert.Equal(["https://help.mikrotik.com/docs/x", "https://a.example/b", "https://c.example/d"], urls);
        Assert.EndsWith(")", ((MdText)inlines[^1]).Text);
        Assert.Equal(3, Inline("a\nb<br>c").Count(i => i is MdLineBreak) + 1);
    }

    [Fact]
    public void ReleaseNotesStructure()
    {
        const string notes = "Switch Pilot 1.1.0 devient **multi-constructeurs**.\r\n\r\n### Télécharger\r\n\r\n- **SwitchPilot.exe** : application.\r\n- **SHA256SUMS.txt** : sommes.\r\n\r\n### Nouveautés\r\n\r\n- **API** : pipeline :\r\n  - simulation ;\r\n  - sauvegarde.\r\n- Plateformes\r\n\r\n| Plateforme | Validation |\r\n| --- | :---: |\r\n| Huawei VRP | `save` |\r\n| Junos \\| EX | `commit` |\r\n\r\n1. un\r\n2. deux\r\n\r\n> note\r\n> suite\r\n\r\n---\r\n\r\n```\r\nshow version\r\n  **brut**\r\n```\r\nFin";
        var blocks = Markdown.Parse(notes);
        Assert.Equal(["MdParagraph", "MdHeading", "MdList", "MdHeading", "MdList", "MdTable", "MdList", "MdQuote", "MdRule", "MdCodeBlock", "MdParagraph"], blocks.Select(b => b.GetType().Name));
        Assert.Equal(3, ((MdHeading)blocks[1]).Level);
        var download = (MdList)blocks[2];
        Assert.False(download.Ordered);
        Assert.Equal(2, download.Items.Count);
        var features = (MdList)blocks[4];
        Assert.Equal(2, features.Items.Count);
        var nested = (MdList)features.Items[0][1];
        Assert.Equal(["simulation ;", "sauvegarde."], nested.Items.Select(i => Markdown.PlainText(((MdParagraph)i.Single()).Content)));
        var table = (MdTable)blocks[5];
        Assert.Equal([MdAlign.Left, MdAlign.Center], table.Align);
        Assert.Equal("Junos | EX", Markdown.PlainText(table.Rows[1][0]));
        Assert.IsType<MdCode>(table.Rows[1][1].Single());
        Assert.Equal(1, ((MdList)blocks[6]).Start);
        Assert.True(((MdList)blocks[6]).Ordered);
        Assert.Equal("note\nsuite", Markdown.PlainText(((MdParagraph)((MdQuote)blocks[7]).Blocks.Single()).Content));
        Assert.Equal("show version\n  **brut**", ((MdCodeBlock)blocks[9]).Code);
    }

    [Fact]
    public void ParagraphsHeadingsAndLooseLists()
    {
        var blocks = Markdown.Parse("Titre\n=====\nligne un\nligne deux\n\n## Sous-titre ##\n\n* a\n\n* b\n\n3) trois\n4) quatre\n#pas un titre");
        Assert.Equal(1, ((MdHeading)blocks[0]).Level);
        Assert.Equal("ligne un\nligne deux", Markdown.PlainText(((MdParagraph)blocks[1]).Content));
        Assert.Equal("Sous-titre", Markdown.PlainText(((MdHeading)blocks[2]).Content));
        Assert.Equal(2, ((MdList)blocks[3]).Items.Count);
        Assert.Equal(3, ((MdList)blocks[4]).Start);
        Assert.Equal("quatre\n#pas un titre", Markdown.PlainText(((MdParagraph)((MdList)blocks[4]).Items[1].Single()).Content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("**")]
    [InlineData("[a](")]
    [InlineData("```\nnon fermé")]
    [InlineData("| a |\n| - |")]
    [InlineData("- \n-\n  - ")]
    [InlineData("`")]
    [InlineData("***\n___")]
    [InlineData("<script>alert(1)</script>")]
    public void MalformedInputNeverThrows(string? text)
    {
        var blocks = Markdown.Parse(text);
        Assert.NotNull(blocks);
    }
}
