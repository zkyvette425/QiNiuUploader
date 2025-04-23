// See https://aka.ms/new-console-template for more information

using SendVideo;

FileMonitor monitor = new FileMonitor();
monitor.StartMonitoring();

Console.WriteLine("anykey");
Console.ReadKey();