using HADA.Service;

if (ServiceHost.IsUnwantedSecondCopy())
{
    Console.Error.WriteLine(
        "Another HADA service is already running on this computer, and two would fight over the connection to "
        + "Home Assistant. Stop it first (in an elevated terminal: sc.exe stop HADA), then start this copy again.");
    return 1;
}

ServiceHost.CreateBuilder(args).Build().Run();
return 0;
