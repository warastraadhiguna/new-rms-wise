using Pos.Desktop.Configuration;

namespace Pos.Desktop.Tests;

public class DesktopSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pos-desktop-tests", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Default_location_is_under_programdata()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DesktopSettingsStore.DefaultPath);
        Assert.EndsWith(Path.Combine("PosDesktop", "settings.json"), DesktopSettingsStore.DefaultPath);
    }

    [Fact]
    public void Missing_file_creates_a_template_and_explains_what_to_fill()
    {
        var result = new DesktopSettingsStore(PathName).Load();

        Assert.Null(result.Settings);
        Assert.True(File.Exists(PathName));
        Assert.Contains(PathName, result.Error);
        Assert.Contains("device_id", File.ReadAllText(PathName));
    }

    [Fact]
    public void Valid_file_is_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, """{"node_service_url":"http://192.168.1.10:5080","device_id":"01930000-0000-7000-8000-0000000000d1"}""");

        var result = new DesktopSettingsStore(PathName).Load();

        Assert.Null(result.Error);
        Assert.Equal("http://192.168.1.10:5080/", result.Settings!.NodeServiceUrl);
        Assert.Equal(Guid.Parse("01930000-0000-7000-8000-0000000000d1"), result.Settings.DeviceId);
    }

    [Theory]
    [InlineData("""{"node_service_url":"","device_id":"01930000-0000-7000-8000-0000000000d1"}""", "node_service_url")]
    [InlineData("""{"node_service_url":"bukan url","device_id":"01930000-0000-7000-8000-0000000000d1"}""", "node_service_url")]
    [InlineData("""{"node_service_url":"http://localhost:5080","device_id":""}""", "device_id")]
    [InlineData("""{"node_service_url":"http://localhost:5080","device_id":"abc"}""", "device_id")]
    [InlineData("not json", "tidak dapat dibaca")]
    public void Invalid_values_are_explained(string json, string mentioned)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathName, json);

        var result = new DesktopSettingsStore(PathName).Load();

        Assert.Null(result.Settings);
        Assert.Contains(mentioned, result.Error);
    }
}
