using Anvil.Core.IO;

namespace Anvil.Core.Tests.IO;

public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 4, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("ap-retention-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FileInfo Log(string name, TimeSpan age)
    {
        var file = Path.Combine(_dir, name);
        File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(file, (Now - age).UtcDateTime);
        return new FileInfo(file);
    }

    [Fact]
    public void Logs_older_than_14_days_expired()
    {
        FileInfo[] files =
        [
            Log("error.2026-10-12.log", TimeSpan.FromDays(15)),
            Log("error.2026-10-13.log", TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1)),
            Log("error.2026-10-14.log", TimeSpan.FromDays(13)),
            Log("error.2026-10-27.log", TimeSpan.Zero),
        ];

        var expired = Retention.Expired(files, Now, TimeSpan.FromDays(14));

        Assert.Equal([files[0].FullName, files[1].FullName], expired);
    }
}
