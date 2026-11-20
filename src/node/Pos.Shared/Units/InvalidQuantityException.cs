namespace Pos.Shared.Units;

/// <summary>
/// A quantity that the unit cannot hold, e.g. 1.5 PCS when PCS does not allow decimals.
/// </summary>
public sealed class InvalidQuantityException(string message, string? paramName = null)
    : ArgumentException(message, paramName);
