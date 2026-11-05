using System.Text.Json.Nodes;
using AiPhoto.Core.Translation;

namespace AiPhoto.Core.Tests.Translation;

public class IncrementalJsonArrayParserTests
{
    private const string Sample = """
        [
          {"box_2d": [10, 20, 30, 40], "source": "Hello [world] {x}", "translation": "你好 \"世界\" ]}"},
          {"box_2d": [[1, 2], [3, 4]], "source": "a\\\"b\\\\", "translation": "c,d"},
          {"nested": {"deep": [{"k": "]"}]}, "source": "s", "translation": "t"}
        ]
        """;

    private static List<string> FeedInChunks(IncrementalJsonArrayParser parser, string text, int chunkSize)
    {
        var result = new List<string>();
        for (var i = 0; i < text.Length; i += chunkSize)
            result.AddRange(parser.Feed(text.Substring(i, Math.Min(chunkSize, text.Length - i))));
        return result;
    }

    private static void AssertSampleElements(IReadOnlyList<string> elements)
    {
        Assert.Equal(3, elements.Count);
        var first = JsonNode.Parse(elements[0])!;
        Assert.Equal("Hello [world] {x}", first["source"]!.GetValue<string>());
        Assert.Equal("你好 \"世界\" ]}", first["translation"]!.GetValue<string>());
        Assert.Equal(4, first["box_2d"]!.AsArray().Count);

        var second = JsonNode.Parse(elements[1])!;
        Assert.Equal("a\\\"b\\\\", second["source"]!.GetValue<string>());
        Assert.Equal(2, second["box_2d"]!.AsArray().Count);
        Assert.Equal("c,d", second["translation"]!.GetValue<string>());

        var third = JsonNode.Parse(elements[2])!;
        Assert.Equal("]", third["nested"]!["deep"]![0]!["k"]!.GetValue<string>());
    }

    [Fact]
    public void WholeArrayInOneChunk()
    {
        var parser = new IncrementalJsonArrayParser();
        var elements = parser.Feed(Sample);
        AssertSampleElements(elements);
        Assert.True(parser.ArrayEnded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void ElementsSplitAcrossArbitraryChunkBoundaries(int chunkSize)
    {
        var parser = new IncrementalJsonArrayParser();
        AssertSampleElements(FeedInChunks(parser, Sample, chunkSize));
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void ElementIsReturnedAsSoonAsItCompletes()
    {
        var parser = new IncrementalJsonArrayParser();
        Assert.Empty(parser.Feed("[{\"source\": \"a\""));
        Assert.Empty(parser.Feed(", \"translation\": \"b\""));
        var done = parser.Feed("}, {\"source\"");
        Assert.Single(done);
        Assert.Equal("{\"source\": \"a\", \"translation\": \"b\"}", done[0]);
        Assert.False(parser.ArrayEnded);
        Assert.Single(parser.Feed(": \"c\"}"));
        Assert.Empty(parser.Feed("]"));
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void LeadingMarkdownFence_IsIgnored()
    {
        var parser = new IncrementalJsonArrayParser();
        var text = "```json\n[{\"source\": \"x\", \"translation\": \"y\"}]\n```";
        var elements = FeedInChunks(parser, text, 1);
        Assert.Single(elements);
        Assert.Equal("y", JsonNode.Parse(elements[0])!["translation"]!.GetValue<string>());
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void TextAfterArrayEnd_IsIgnored()
    {
        var parser = new IncrementalJsonArrayParser();
        Assert.Single(parser.Feed("[{\"a\":1}] trailing [{\"b\":2}]"));
        Assert.Empty(parser.Feed("[{\"c\":3}]"));
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void EmptyArray()
    {
        var parser = new IncrementalJsonArrayParser();
        Assert.Empty(parser.Feed("[ \n ]"));
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void NoArrayYet_ReturnsNothing()
    {
        var parser = new IncrementalJsonArrayParser();
        Assert.Empty(parser.Feed("Sure! Here is the result:\n"));
        Assert.False(parser.ArrayEnded);
        Assert.Single(parser.Feed("[{\"a\":\"b\"}"));
    }

    [Fact]
    public void TopLevelNestedArraysAndStrings()
    {
        var parser = new IncrementalJsonArrayParser();
        var elements = FeedInChunks(parser, "[[1,[2,3]], \"str]ing\", {\"x\":[]}]", 1);
        Assert.Equal(["[1,[2,3]]", "\"str]ing\"", "{\"x\":[]}"], elements);
        Assert.True(parser.ArrayEnded);
    }

    [Fact]
    public void UnicodeEscapesAndEscapedBackslashBeforeQuote()
    {
        var parser = new IncrementalJsonArrayParser();
        // "\\" ends with an escaped backslash, so the following quote closes the string.
        var text = "[{\"s\":\"\\u4f60\\\\\"},{\"s\":\"}\"}]";
        var elements = FeedInChunks(parser, text, 1);
        Assert.Equal(2, elements.Count);
        Assert.Equal("你\\", JsonNode.Parse(elements[0])!["s"]!.GetValue<string>());
        Assert.Equal("}", JsonNode.Parse(elements[1])!["s"]!.GetValue<string>());
    }

    [Fact]
    public void TopLevelNumbersAndLiterals_AreReturned()
    {
        var parser = new IncrementalJsonArrayParser();
        var elements = FeedInChunks(parser, "[1, -2.5e3,true , null,{\"a\":1}]", 1);
        Assert.Equal(["1", "-2.5e3", "true", "null", "{\"a\":1}"], elements);
        Assert.True(parser.ArrayEnded);
    }
}
