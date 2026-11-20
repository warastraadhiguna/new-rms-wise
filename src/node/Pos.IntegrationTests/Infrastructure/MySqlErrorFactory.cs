using System.Reflection;
using MySqlConnector;

namespace Pos.IntegrationTests.Infrastructure;

/// <summary>MySqlException has no public constructor; build one by reflection for retry tests.</summary>
public static class MySqlErrorFactory
{
    public static MySqlException Create(MySqlErrorCode code)
    {
        var constructor = typeof(MySqlException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c => c.GetParameters() is [{ ParameterType: var first }, ..] && first == typeof(MySqlErrorCode))
            .OrderBy(c => c.GetParameters().Length)
            .First();

        var args = constructor.GetParameters()
            .Select(p => p.ParameterType == typeof(MySqlErrorCode) ? code
                : p.ParameterType == typeof(string) ? $"Simulated MySQL error {(int)code}"
                : (object?)null)
            .ToArray();

        return (MySqlException)constructor.Invoke(args);
    }
}
