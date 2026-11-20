namespace Pos.Desktop.Tests;

/// <summary>
/// Tests that create real WPF windows run one after another, never in parallel with each other,
/// so their STA threads and windows cannot interfere.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfUiCollection
{
    public const string Name = "WPF UI";
}
