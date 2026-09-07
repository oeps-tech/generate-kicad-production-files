using System.Reflection;

namespace Oeps.KicadProductionFiles.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public static class Assert
{
    public static void True(bool actual, string? message = null) { if (!actual) throw new Exception(message ?? "Expected true."); }
    public static void False(bool actual, string? message = null) => True(!actual, message ?? "Expected false.");
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected <{expected}>, got <{actual}>."); }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T e) { return e; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T e) { return e; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-live") return await IntegrationChecks.VerifyLiveAsync(args[1]);
        if (args.Length is 2 or 3 && args[0] == "--verify-configuration")
            return await IntegrationChecks.VerifyConfigurationAsync(args[1], args.Length == 3 ? args[2] : "");

        int passed = 0, failed = 0;
        foreach (var method in Assembly.GetExecutingAssembly().GetTypes().SelectMany(t => t.GetMethods()).Where(m => m.GetCustomAttribute<TestAttribute>() != null).OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name))
        {
            try
            {
                var result = method.Invoke(null, null);
                if (result is Task task) await task;
                Console.WriteLine($"PASS {method.DeclaringType!.Name}.{method.Name}"); passed++;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"FAIL {method.DeclaringType!.Name}.{method.Name}: {(e is TargetInvocationException ? e.InnerException : e)}"); failed++;
            }
        }
        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}
