using HADA.Core;
using HADA.Service;

if (ServiceHost.IsUnwantedSecondCopy())
{
    Console.Error.WriteLine(
        "Another HADA service is already running on this computer, and two would fight over the connection to "
        + "Home Assistant. Stop it first (in an elevated terminal: sc.exe stop HADA), then start this copy again.");
    return 1;
}

// The service of a portable copy is started by its tray app; started twice, the second has nothing to do.
using var onlyOne = AppInstance.IsPortable ? new Mutex(initiallyOwned: false, @"Local\HADA.Service" + AppInstance.Suffix) : null;
if (onlyOne is not null && !IsFirstInstance(onlyOne))
{
    return 0;
}

ServiceHost.CreateBuilder(args).Build().Run();
return 0;

// Held until this process ends; while another process holds it, this one is not the first.
static bool IsFirstInstance(Mutex mutex)
{
    try
    {
        return mutex.WaitOne(0);
    }
    catch (AbandonedMutexException)
    {
        // The previous owner was killed; the mutex is ours now.
        return true;
    }
}
