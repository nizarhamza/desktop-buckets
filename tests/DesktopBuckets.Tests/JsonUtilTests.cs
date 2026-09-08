using System.Collections.Generic;
using System.IO;
using DesktopBuckets.Services;
using Xunit;

namespace DesktopBuckets.Tests
{
    public class JsonUtilTests
    {
        private sealed class Doc
        {
            public string Name { get; set; } = "";
            public List<string> Items { get; set; } = new();
            public double? Maybe { get; set; }
        }

        [Fact]
        public void RoundTrips()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "d.json");
            JsonUtil.Write(path, new Doc { Name = "n", Items = { "a", "b" }, Maybe = 1.5 });

            var back = JsonUtil.Read<Doc>(path)!;
            Assert.Equal("n", back.Name);
            Assert.Equal(new[] { "a", "b" }, back.Items);
            Assert.Equal(1.5, back.Maybe);
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void WriteCreatesMissingDirectory()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "deep", "er", "d.json");
            JsonUtil.Write(path, new Doc { Name = "x" });
            Assert.Equal("x", JsonUtil.Read<Doc>(path)!.Name);
        }

        [Fact]
        public void HiddenAttributeSurvivesRewrite()
        {
            using var tmp = new TempDir();
            var path = Path.Combine(tmp.Path, "d.json");
            JsonUtil.WriteHidden(path, new Doc { Name = "1" });
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

            JsonUtil.Write(path, new Doc { Name = "2" });
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
            Assert.Equal("2", JsonUtil.Read<Doc>(path)!.Name);
        }

        [Fact]
        public void ReadReturnsDefaultForMissingEmptyOrCorruptFiles()
        {
            using var tmp = new TempDir();
            Assert.Null(JsonUtil.Read<Doc>(Path.Combine(tmp.Path, "missing.json")));
            Assert.Null(JsonUtil.Read<Doc>(tmp.File("empty.json", "   ")));
            Assert.Null(JsonUtil.Read<Doc>(tmp.File("corrupt.json", "{ \"Name\": ")));
        }

        [Fact]
        public void ReadIsCaseInsensitiveOnPropertyNames()
        {
            using var tmp = new TempDir();
            var p = tmp.File("d.json", "{ \"name\": \"lower\", \"ITEMS\": [\"q\"] }");
            var d = JsonUtil.Read<Doc>(p)!;
            Assert.Equal("lower", d.Name);
            Assert.Equal(new[] { "q" }, d.Items);
        }
    }
}
