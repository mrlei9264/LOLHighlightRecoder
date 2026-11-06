using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public class MetadataFooterServiceTests
{
    private readonly MetadataFooterService _service = new();

    [Fact]
    public void AppendMetadataFooter_WritesToFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var metadata = new Dictionary<string, object>
            {
                ["type"] = "highlight",
                ["player"] = new Dictionary<string, string>
                {
                    ["game_name"] = "TestPlayer",
                    ["tag_line"] = "TAG"
                },
                ["event"] = new Dictionary<string, object>
                {
                    ["kind"] = "kill",
                    ["label"] = "击杀 Enemy"
                }
            };

            var size = _service.AppendMetadataFooter(tempFile, metadata);

            Assert.True(size > 0);
            Assert.True(new FileInfo(tempFile).Length > 0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ReadMetadataFooter_ReadsAppendedData()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var metadata = new Dictionary<string, object>
            {
                ["type"] = "highlight",
                ["player"] = new Dictionary<string, string>
                {
                    ["game_name"] = "TestPlayer",
                    ["tag_line"] = "TAG"
                },
                ["event"] = new Dictionary<string, object>
                {
                    ["kind"] = "kill",
                    ["label"] = "击杀 Enemy"
                }
            };

            _service.AppendMetadataFooter(tempFile, metadata);
            var result = _service.ReadMetadataFooter(tempFile);

            Assert.NotNull(result);
            Assert.Equal("highlight", result["type"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ReadMetadataFooter_NoFooter_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var result = _service.ReadMetadataFooter(tempFile);

            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void AppendMetadataFooter_MultipleAppends_ReadsLastFooter()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var metadata1 = new Dictionary<string, object>
            {
                ["type"] = "first",
                ["value"] = 1
            };

            var metadata2 = new Dictionary<string, object>
            {
                ["type"] = "second",
                ["value"] = 2
            };

            _service.AppendMetadataFooter(tempFile, metadata1);
            _service.AppendMetadataFooter(tempFile, metadata2);

            var result = _service.ReadMetadataFooter(tempFile);

            Assert.NotNull(result);
            // 读取到最后一个footer
            Assert.Equal("second", result["type"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ReadMetadataFooter_WithChineseCharacters_ReadsCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var metadata = new Dictionary<string, object>
            {
                ["type"] = "highlight",
                ["player"] = new Dictionary<string, string>
                {
                    ["game_name"] = "测试玩家",
                    ["tag_line"] = "标签"
                },
                ["event"] = new Dictionary<string, object>
                {
                    ["kind"] = "kill",
                    ["label"] = "击杀 敌人"
                }
            };

            _service.AppendMetadataFooter(tempFile, metadata);
            var result = _service.ReadMetadataFooter(tempFile);

            Assert.NotNull(result);
            var player = (Dictionary<string, object>)result["player"];
            Assert.Equal("测试玩家", player["game_name"]);
            Assert.Equal("标签", player["tag_line"]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
