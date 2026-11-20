using Pos.NodeService.Auth;

namespace Pos.IntegrationTests;

/// <summary>No database needed.</summary>
public class ExpiringCacheTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();
    private int _loads;

    private Task<string?> Load(string key)
    {
        _loads++;
        return Task.FromResult<string?>($"{key}#{_loads}");
    }

    [Fact]
    public async Task Value_is_reused_within_the_ttl()
    {
        var cache = new ExpiringCache<string, string?>(TimeSpan.FromSeconds(30), _clock);

        var first = await cache.GetOrLoadAsync("a", Load);
        _clock.Now = _clock.Now.AddSeconds(29);
        var second = await cache.GetOrLoadAsync("a", Load);

        Assert.Equal(first, second);
        Assert.Equal(1, _loads);
    }

    [Fact]
    public async Task Value_is_reloaded_after_the_ttl()
    {
        var cache = new ExpiringCache<string, string?>(TimeSpan.FromSeconds(30), _clock);

        await cache.GetOrLoadAsync("a", Load);
        _clock.Now = _clock.Now.AddSeconds(30);
        var reloaded = await cache.GetOrLoadAsync("a", Load);

        Assert.Equal("a#2", reloaded);
    }

    [Fact]
    public async Task Keys_are_independent_and_null_is_cached_too()
    {
        var cache = new ExpiringCache<string, string?>(TimeSpan.FromSeconds(30), _clock);

        await cache.GetOrLoadAsync("a", Load);
        await cache.GetOrLoadAsync("b", Load);
        await cache.GetOrLoadAsync("missing", _ => { _loads++; return Task.FromResult<string?>(null); });
        await cache.GetOrLoadAsync("missing", _ => { _loads++; return Task.FromResult<string?>(null); });

        Assert.Equal(3, _loads);
    }

    [Fact]
    public async Task Zero_ttl_always_loads()
    {
        var cache = new ExpiringCache<string, string?>(TimeSpan.Zero, _clock);

        await cache.GetOrLoadAsync("a", Load);
        await cache.GetOrLoadAsync("a", Load);

        Assert.Equal(2, _loads);
    }

    [Fact]
    public void Negative_ttl_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExpiringCache<string, string?>(TimeSpan.FromSeconds(-1), _clock));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(10, 10)]
    [InlineData(0, 0)]
    [InlineData(120, 30)]    // never more than 30 s
    [InlineData(-5, 0)]
    public void User_cache_ttl_comes_from_settings_capped_at_30_seconds(int? configured, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), UserAccessCache.TtlFrom(configured));
    }
}
