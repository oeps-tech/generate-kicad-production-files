using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace Oeps.KicadProductionFiles.Core.Updates;

/// <summary>Per-user app instance and activation coordination, independent of the executable version.</summary>
[SupportedOSPlatform("windows")]
public sealed class AppInstanceCoordinator : IDisposable
{
    private static readonly string UserSuffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.UserDomainName + "\\" + Environment.UserName)))[..20];
    public static string AppMutexName => @"Local\OEPS.KicadProductionFiles.App." + UserSuffix;
    public static string LauncherMutexName => @"Local\OEPS.KicadProductionFiles.Launcher." + UserSuffix;
    private static string ActivationEventName => @"Local\OEPS.KicadProductionFiles.Activate." + UserSuffix;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly RegisteredWaitHandle _registration;

    private AppInstanceCoordinator(Mutex mutex, Action activate)
    {
        _mutex = mutex;
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activate(), null, Timeout.Infinite, false);
    }

    /// <summary>Call on startup. Return null means another instance was asked to activate; exit this process.</summary>
    public static AppInstanceCoordinator? TryAcquire(Action activate)
    {
        var mutex = new Mutex(false, AppMutexName, out var created);
        if (created) return new(mutex, activate);
        mutex.Dispose();
        ActivateExisting();
        return null;
    }

    public static bool IsAppRunning()
    {
        if (!Mutex.TryOpenExisting(AppMutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static void ActivateExisting()
    {
        if (!EventWaitHandle.TryOpenExisting(ActivationEventName, out var activation)) return;
        using (activation) activation.Set();
    }

    public static void SignalReadyFromArguments(string[] args)
    {
        var index = Array.IndexOf(args, "--startup-ready");
        if (index < 0 || index + 1 >= args.Length) return;
        if (!args[index + 1].StartsWith(@"Local\OEPS.KicadProductionFiles.Ready.", StringComparison.Ordinal)) return;
        if (EventWaitHandle.TryOpenExisting(args[index + 1], out var ready)) using (ready) ready.Set();
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _activation.Dispose();
        _mutex.Dispose();
    }
}
