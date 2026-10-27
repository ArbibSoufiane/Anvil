using ArbiPache.Core.IO;

namespace ArbiPache.Core.Tests.IO;

public sealed class IniDocumentTests
{
    private const string Sample =
        "[PHP]\r\n" +
        "; About php.ini   ;\r\n" +
        "\r\n" +
        "  engine   =   On  \r\n" +
        ";memory_limit = 128M\r\n" +
        ";extension=curl\r\n" +
        "extension=gd\r\n" +
        "\r\n" +
        "[Session]\r\n" +
        ";session.save_path = \"/tmp\"\r\n" +
        "session.name = PHPSESSID";

    [Fact]
    public void Untouched_lines_round_trip_byte_identical()
    {
        Assert.Equal(Sample, IniDocument.Parse(Sample).ToString());

        var doc = IniDocument.Parse(Sample);
        doc.Set("PHP", "memory_limit", "512M");

        var before = Sample.Split("\r\n");
        var after = doc.ToString().Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        Assert.Equal("memory_limit = 512M", after[4]);
        Assert.Equal(before.Where((_, i) => i != 4), after.Where((_, i) => i != 4));
    }

    [Fact]
    public void Set_uncomments_existing_key()
    {
        var doc = IniDocument.Parse(Sample);

        doc.Set("PHP", "memory_limit", "512M");

        Assert.Equal("512M", doc.Get("PHP", "memory_limit"));
        Assert.DoesNotContain(";memory_limit", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_replaces_active_line()
    {
        var doc = IniDocument.Parse(Sample);

        doc.Set("PHP", "engine", "Off");

        Assert.Equal("Off", doc.Get("PHP", "engine"));
        Assert.Contains("\r\nengine = Off\r\n", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_appends_to_end_of_its_section()
    {
        var doc = IniDocument.Parse(Sample);

        doc.Set("PHP", "max_execution_time", "30");
        doc.Set("Date", "date.timezone", "UTC");

        var text = doc.ToString();
        Assert.Contains("extension=gd\r\nmax_execution_time = 30\r\n\r\n[Session]", text, StringComparison.Ordinal);
        Assert.EndsWith("session.name = PHPSESSID\r\n[Date]\r\ndate.timezone = UTC\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_valued_extension_lines_supported()
    {
        var doc = IniDocument.Parse(Sample);
        Assert.Equal(["gd"], doc.GetAll("PHP", "extension"));

        doc.AddValue("PHP", "extension", "curl");
        doc.AddValue("PHP", "extension", "curl");
        doc.AddValue("PHP", "extension", "zip");
        Assert.Equal(["curl", "gd", "zip"], doc.GetAll("PHP", "extension"));

        doc.RemoveValue("PHP", "extension", "gd");
        Assert.Equal(["curl", "zip"], doc.GetAll("PHP", "extension"));
        Assert.Contains("\r\n;extension=gd\r\n", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_case_insensitive()
    {
        var doc = IniDocument.Parse("[php]\nMemory_Limit = 1G\n");

        Assert.Equal("1G", doc.Get("PHP", "MEMORY_LIMIT"));
        Assert.Null(doc.Get("PHP", "missing"));
    }

    [Fact]
    public void Get_unquotes_values()
    {
        var doc = IniDocument.Parse("[PHP]\nextension_dir = \"C:/php/ext\"\n");

        Assert.Equal("C:/php/ext", doc.Get("PHP", "extension_dir"));
    }
}
