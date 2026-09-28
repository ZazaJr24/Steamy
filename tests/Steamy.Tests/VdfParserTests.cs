using Steamy.Services;

namespace Steamy.Tests;

public class VdfParserTests
{
    [Fact]
    public void Nested_blocks_comments_and_escapes_are_read()
    {
        const string text = """
            // library file
            "libraryfolders"
            {
                "0"
                {
                    "path"    "C:\\Program Files (x86)\\Steam"
                    "apps" { "730" "123" }
                }
            }
            """;

        var root = VdfParser.Parse(text);
        var library = root["libraryfolders"]!["0"]!;

        Assert.Equal(@"C:\Program Files (x86)\Steam", library.GetString("path"));
        Assert.Equal(123, library["apps"]!.GetInt("730"));
    }

    [Fact]
    public void Broken_input_never_throws()
    {
        var root = VdfParser.Parse("\"AppState\" { \"appid\" \"10\" \"name\"");

        Assert.Equal(10, root["AppState"]!.GetInt("appid"));
        Assert.Equal(0, VdfParser.Parse(null).GetInt("missing"));
    }
}
